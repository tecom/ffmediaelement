namespace Unosquare.FFME.Platform
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// A wrapper class for shared memory
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    internal sealed class SharedMemory : IDisposable
    {
        /// <summary>
        /// The no file handle - (i.e. virtual memory)
        /// </summary>
        private static readonly IntPtr NoFileHandle = new IntPtr(-1);

        private bool IsDisposed = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedMemory"/> class.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <param name="openExisting">if set to <c>true</c> [open existing].</param>
        /// <param name="length">The length.</param>
        /// <exception cref="Exception">
        /// Open/create error: " + Marshal.GetLastWin32Error()
        /// or
        /// MapViewOfFile error: " + Marshal.GetLastWin32Error()
        /// </exception>
        private SharedMemory(Guid id, bool openExisting, int length)
        {
            var name = id.ToString();

            if (openExisting)
                Handle = NativeMethods.OpenFileMapping(FileRights.ReadWrite, false, name);
            else
                Handle = NativeMethods.CreateFileMapping(NoFileHandle, IntPtr.Zero, FileProtection.ReadWrite, 0, (uint)length, name);

            if (Handle == IntPtr.Zero)
                throw new ExternalException("Failed to create shared memory.");

            // Obtain a read/write map for the entire file
            Data = NativeMethods.MapViewOfFile(Handle, FileRights.ReadWrite, 0, 0, IntPtr.Zero);

            if (Data == IntPtr.Zero)
                throw new ExternalException("Failed to map shared memory.");

            Id = id;
            Length = length;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="SharedMemory"/> class.
        /// </summary>
        ~SharedMemory()
        {
            Dispose(false);
        }

        /// <summary>
        /// Enumerates Win32 File Protection
        /// </summary>
        private enum FileProtection : uint
        {
            ReadOnly = 2,
            ReadWrite = 4
        }

        /// <summary>
        /// Enumerates Win32 File Rights
        /// </summary>
        private enum FileRights : uint
        {
            Read = 4,
            Write = 2,
            ReadWrite = Read + Write
        }

        /// <summary>
        /// Gets the unique identifier.
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// Gets the length of assigned memory.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Gets the handle to the memory -- not the data itself.
        /// </summary>
        public IntPtr Handle { get; }

        /// <summary>
        /// Gets a pointer to the first data byte in memory.
        /// </summary>
        public IntPtr Data { get; }

        /// <summary>
        /// Creates a Shared Memory area with the specified length.
        /// </summary>
        /// <param name="length">The length.</param>
        /// <returns>A newly-created memory area</returns>
        public static SharedMemory Create(int length)
        {
            return new SharedMemory(Guid.NewGuid(), false, length);
        }

        /// <summary>
        /// Opens the specified Shared memory area
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns>A reference to the ssahred memory</returns>
        public static SharedMemory Open(Guid id)
        {
            return new SharedMemory(id, true, 0);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        private void Dispose(bool alsoManaged)
        {
            if (!IsDisposed)
            {
                if (alsoManaged)
                {
                    // placeholder: dispose managed state (managed objects).
                }

                // free unmanaged resources (unmanaged objects) and override a finalizer below.
                if (Data != IntPtr.Zero)
                    NativeMethods.UnmapViewOfFile(Data);

                if (Handle != IntPtr.Zero)
                    NativeMethods.CloseHandle(Handle);

                IsDisposed = true;
            }
        }

        /// <summary>
        /// Encloses Win32 APIs
        /// </summary>
        private static class NativeMethods
        {
            private const string Kernel32 = "kernel32.dll";

            [DllImport(Kernel32)]
            public static extern int CloseHandle(IntPtr hObject);

            [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Auto)]
            public static extern IntPtr CreateFileMapping(
                IntPtr hFile,
                IntPtr lpFileMappingAttributes,
                FileProtection flProtect,
                uint dwMaximumSizeHigh,
                uint dwMaximumSizeLow,
                [MarshalAs(UnmanagedType.LPWStr)] string lpName);

            [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Auto)]
            public static extern IntPtr OpenFileMapping(
                FileRights dwDesiredAccess,
                bool bInheritHandle,
                [MarshalAs(UnmanagedType.LPWStr)] string lpName);

            [DllImport(Kernel32, SetLastError = true)]
            public static extern IntPtr MapViewOfFile(
                IntPtr hFileMappingObject,
                FileRights dwDesiredAccess,
                uint dwFileOffsetHigh,
                uint dwFileOffsetLow,
                IntPtr dwNumberOfBytesToMap);

            [DllImport(Kernel32)]
            public static extern bool UnmapViewOfFile(IntPtr map);
        }
    }
}
