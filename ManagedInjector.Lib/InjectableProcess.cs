using System;
using System.Diagnostics;
using System.Linq;
using HoLLy.ManagedInjector.Injectors;

namespace HoLLy.ManagedInjector
{
	public class InjectableProcess : IDisposable
	{
		private const Native.ProcessAccessFlags FlagsForInject = Native.ProcessAccessFlags.CreateThread |
		                                                         Native.ProcessAccessFlags.QueryInformation |
		                                                         Native.ProcessAccessFlags.VirtualMemoryOperation |
		                                                         Native.ProcessAccessFlags.VirtualMemoryRead |
		                                                         Native.ProcessAccessFlags.VirtualMemoryWrite;

		private const Native.ProcessAccessFlags BasicFlags = Native.ProcessAccessFlags.QueryInformation;

		private readonly int _pid;
		private readonly Process _process;
		private IntPtr _handle;
		private bool _isHandleFull;
		private bool? _is64Bit;
		private ProcessStatus _status = ProcessStatus.Unknown;
		private ProcessArchitecture _architecture = ProcessArchitecture.Unknown;

		public InjectableProcess(int pid)
		{
			_pid = pid;
			_process = Process.GetProcessById(pid);
		}

		public int Pid => _pid;
		public bool Is64Bit => _is64Bit ??= NativeHelper.Is64BitProcess(_handle);

		/// <summary>
		/// Get a handle to the process. This is only guaranteed to have basic flags set.
		/// </summary>
		public IntPtr Handle
		{
			get
			{
				if (_handle == IntPtr.Zero)
					_handle = NativeHelper.OpenProcess(BasicFlags, _pid);

				return _handle;
			}
		}

		/// <summary>
		/// Gets a handle to the process that is guaranteed to have more flags set.
		/// </summary>
		public IntPtr FullHandle
		{
			get
			{
				if (!_isHandleFull)
				{
					Native.CloseHandle(_handle);
					_handle = IntPtr.Zero;
				}

				if (_handle == IntPtr.Zero)
				{
					_handle = NativeHelper.OpenProcess(FlagsForInject, _pid);
					_isHandleFull = true;
				}

				return _handle;
			}
		}

		public ProcessStatus GetStatus()
		{
			if (_status != ProcessStatus.Unknown)
				return _status;

			if (NativeHelper.In64BitProcess != NativeHelper.Is64BitProcess(Handle))
				return _status = ProcessStatus.ArchitectureMismatch;

			if (GetArchitecture() == ProcessArchitecture.Unknown)
				return _status = ProcessStatus.NoRuntimeFound;

			return _status = ProcessStatus.Ok;
		}

		public ProcessArchitecture GetArchitecture()
		{
			if (_architecture != ProcessArchitecture.Unknown)
				return _architecture;

			using var process = Process.GetProcessById(_pid);

			bool HasModule(string s) => process.Modules.OfType<ProcessModule>()
				.Any(x => x.ModuleName.Equals(s, StringComparison.InvariantCultureIgnoreCase));

			// .NET 2 has mscoree and mscorwks
			// .NET 4 has mscoree and clr
			// .NET Core 3.1 has coreclr
			// Some unity games have mono-2.0-bdwgc.dll

			if (HasModule("mscoree.dll"))
			{
				if (HasModule("clr.dll"))
					return _architecture = ProcessArchitecture.NetFrameworkV4;

				if (HasModule("mscorwks.dll"))
					return _architecture = ProcessArchitecture.NetFrameworkV2;
			}

			if (HasModule("coreclr.dll"))
				return _architecture = ProcessArchitecture.NetCore;

			// TODO: also check non-bleeding mono dll
			if (HasModule("mono-2.0-bdwgc.dll"))
				return _architecture = ProcessArchitecture.Mono;

			return ProcessArchitecture.Unknown;
		}

		public void Inject(string dllPath, string typeName, string methodName)
		{
			var arch = GetArchitecture();
			IInjector injector = arch switch
			{
				ProcessArchitecture.NetFrameworkV2 => new FrameworkV2Injector(),
				ProcessArchitecture.NetFrameworkV4 => new FrameworkV4Injector(),
				ProcessArchitecture.Mono => throw new NotImplementedException("mono injector not yet implemented"),
				ProcessArchitecture.NetCore => throw new NotImplementedException("mono injector not yet implemented"),
				ProcessArchitecture.Unknown => throw new Exception(
					"Tried to inject into process with unknown architecture"),
				_ => throw new NotSupportedException($"No injector found for architecture {arch}"),
			};

			Inject(injector, dllPath, typeName, methodName);
		}

		public void Inject(IInjector injector, string dllPath, string typeName, string methodName) =>
			injector.Inject(this, dllPath, typeName, methodName);

		private void ReleaseUnmanagedResources()
		{
			if (_handle != IntPtr.Zero)
				Native.CloseHandle(_handle);
		}

		public void Dispose()
		{
			ReleaseUnmanagedResources();
			GC.SuppressFinalize(this);
		}

		~InjectableProcess()
		{
			ReleaseUnmanagedResources();
		}
	}
}