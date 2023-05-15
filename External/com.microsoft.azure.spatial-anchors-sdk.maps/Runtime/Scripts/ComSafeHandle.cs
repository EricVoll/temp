using System;
using System.Runtime.InteropServices;

namespace Microsoft.Azure.SpatialAnchors
{
    internal class ComSafeHandle : SafeHandle
    {
        public ComSafeHandle()
            : this(IntPtr.Zero)
        {
        }

        public ComSafeHandle(IntPtr handle)
            : base(handle, true)
        {
        }

        public ComSafeHandle(object runtimeCallableWrapper)
            : base(Marshal.GetIUnknownForObject(runtimeCallableWrapper), true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        public object GetRuntimeCallableWrapper()
        {
            if (IsInvalid)
            {
                return null;
            }

            return Marshal.GetObjectForIUnknown(handle);
        }

        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero)
            {
                Marshal.Release(handle);
                handle = IntPtr.Zero;
            }
            return true;
        }
    }
}