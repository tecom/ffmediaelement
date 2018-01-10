namespace Unosquare.FFME.Platform
{
    using FFmpeg.AutoGen;
    using Rendering;
    using Shared;
    using System;
    using System.Collections.Generic;
    using System.Windows;
    using System.Windows.Interop;
    using System.Windows.Threading;

    internal sealed class SharedMemoryBitmap : IDisposable
    {
        private const double DefaultDpi = 96.0;

        /// <summary>
        /// Contains an equivalence lookup of FFmpeg pixel fromat and WPF pixel formats.
        /// </summary>
        private static readonly Dictionary<AVPixelFormat, System.Windows.Media.PixelFormat> MediaPixelFormats
            = new Dictionary<AVPixelFormat, System.Windows.Media.PixelFormat>
        {
            { AVPixelFormat.AV_PIX_FMT_BGR0, System.Windows.Media.PixelFormats.Bgr32 }
        };

        private bool IsDisposed = false; // To detect redundant calls
        private double DpiX = DefaultDpi;
        private double DpiY = DefaultDpi;
        private InteropBitmap RenderBitmapSource = null;
        private SharedMemory InteropMemory = null;
        private int BufferLength = 0;
        private VideoRenderer Renderer;

        public SharedMemoryBitmap(VideoRenderer videoRenderer)
        {
            Renderer = videoRenderer;

            // Get the DPI on the GUI thread
            WindowsPlatform.Instance.Gui?.Invoke(DispatcherPriority.Normal, () =>
            {
                var visual = PresentationSource.FromVisual(Renderer.MediaElement);
                DpiX = DefaultDpi * visual?.CompositionTarget?.TransformToDevice.M11 ?? DefaultDpi;
                DpiY = DefaultDpi * visual?.CompositionTarget?.TransformToDevice.M22 ?? DefaultDpi;
            });
        }

        public void Load(VideoBlock block)
        {
            EnsureLoadable(block);
            if (InteropMemory != null)
            {
                WindowsNativeMethods.Instance.CopyMemory(InteropMemory.Data, block.Buffer, (uint)block.BufferLength);
            }
            else
            {
                // TODO: For some reason, sometimes IteropMemory is null? Investigate more.
            }
        }

        public void Render()
        {
            if (Renderer.MediaElement.ViewBox.Source != RenderBitmapSource)
                Renderer.MediaElement.ViewBox.Source = RenderBitmapSource;

            RenderBitmapSource?.Invalidate();
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void EnsureLoadable(VideoBlock block)
        {
            if (AllocateBuffer(block.BufferLength) == false && RenderBitmapSource != null)
                return;

            // Create the bitmap source on the GUI thread.
            WindowsPlatform.Instance.Gui.Invoke(DispatcherPriority.Normal, () =>
            {
                RenderBitmapSource = Imaging.CreateBitmapSourceFromMemorySection(
                    InteropMemory.Handle,
                    block.PixelWidth,
                    block.PixelHeight,
                    MediaPixelFormats[Defaults.VideoPixelFormat],
                    block.BufferStride,
                    0) as InteropBitmap;
            });
        }

        private bool AllocateBuffer(int length)
        {
            if (BufferLength == length) return false;

            if (BufferLength != length)
                DestroyBuffer();

            InteropMemory = SharedMemory.Create(length);
            BufferLength = length;

            return true;
        }

        private void DestroyBuffer()
        {
            if (InteropMemory == null)
            {
                BufferLength = 0;
                return;
            }

            InteropMemory.Dispose();
            InteropMemory = null;
            BufferLength = 0;
        }

        private void Dispose(bool alsoManaged)
        {
            if (!IsDisposed)
            {
                if (alsoManaged)
                {
                    if (InteropMemory != null)
                    {
                        InteropMemory.Dispose();
                    }
                }

                InteropMemory = null;
                IsDisposed = true;
            }
        }
    }
}
