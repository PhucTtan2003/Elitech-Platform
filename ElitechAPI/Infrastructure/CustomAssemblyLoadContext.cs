using System;
using System.Reflection;
using System.Runtime.Loader;

namespace Elitech.Infrastructure
{
    public class CustomAssemblyLoadContext : AssemblyLoadContext
    {
        public IntPtr LoadUnmanagedLibrary(string absolutePath)
        {
            return LoadUnmanagedDll(absolutePath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            return IntPtr.Zero;
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            return null;
        }
    }
}