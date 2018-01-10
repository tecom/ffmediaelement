namespace Unosquare.FFME.Rendering
{
    using Platform;
    using Primitives;
    using Shared;
    using System;
    using System.Runtime.CompilerServices;
    using System.Windows.Media;
    using System.Windows.Threading;

    /// <summary>
    /// Provides Video Image Rendering via a WPF Writable Bitmap
    /// </summary>
    /// <seealso cref="Unosquare.FFME.Shared.IMediaRenderer" />
    internal sealed class VideoRenderer : IMediaRenderer, IDisposable
    {
        #region Private State

        private bool IsDisposed = false;

        private SharedMemoryBitmap SourceBitmap = null;

        /// <summary>
        /// Set when a bitmap is being written to the target bitmap
        /// </summary>
        private AtomicBoolean IsRenderingInProgress = new AtomicBoolean(false);

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoRenderer"/> class.
        /// </summary>
        /// <param name="mediaEngine">The core media element.</param>
        public VideoRenderer(MediaEngine mediaEngine)
        {
            MediaCore = mediaEngine;
            SourceBitmap = new SharedMemoryBitmap(this);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the parent media element (platform specific).
        /// </summary>
        public MediaElement MediaElement => MediaCore?.Parent as MediaElement;

        /// <summary>
        /// Gets the core platform independent player component.
        /// </summary>
        public MediaEngine MediaCore { get; }

        #endregion

        #region IMediaRenderer Methods

        /// <summary>
        /// Executed when the Play method is called on the parent MediaElement
        /// </summary>
        public void Play()
        {
            // placeholder
        }

        /// <summary>
        /// Executed when the Pause method is called on the parent MediaElement
        /// </summary>
        public void Pause()
        {
            // placeholder
        }

        /// <summary>
        /// Executed when the Pause method is called on the parent MediaElement
        /// </summary>
        public void Stop()
        {
            // placeholder
        }

        /// <summary>
        /// Executed when the Close method is called on the parent MediaElement
        /// </summary>
        public void Close()
        {
            WindowsPlatform.Instance.Gui?.Invoke(DispatcherPriority.Render, () =>
            {
                MediaElement.ViewBox.Source = null;
            });

            Dispose();
        }

        /// <summary>
        /// Executed after a Seek operation is performed on the parent MediaElement
        /// </summary>
        public void Seek()
        {
            // placeholder
        }

        /// <summary>
        /// Waits for the renderer to be ready to render.
        /// </summary>
        public void WaitForReadyState()
        {
            // placeholder
            // we don't need to be ready.
        }

        /// <summary>
        /// Renders the specified media block.
        /// This needs to return immediately so the calling thread is not disturbed.
        /// </summary>
        /// <param name="mediaBlock">The media block.</param>
        /// <param name="clockPosition">The clock position.</param>
        public void Render(MediaBlock mediaBlock, TimeSpan clockPosition)
        {
            var block = mediaBlock as VideoBlock;
            if (block == null) return;

            // Skip if rendering is currently in progress
            if (IsRenderingInProgress.Value == true)
            {
                MediaElement?.MediaCore?.Log(MediaLogMessageType.Debug, $"{nameof(VideoRenderer)}: Frame skipped at {mediaBlock.StartTime}");
                return;
            }

            IsRenderingInProgress.Value = true;
            SourceBitmap.Load(block);

            WindowsPlatform.Instance.Gui?.EnqueueInvoke(
                DispatcherPriority.Render,
                new Action<VideoBlock, TimeSpan>((b, cP) =>
                {
                    try
                    {
                        // Skip rendering if Scrubbing is not enabled
                        if (MediaElement.ScrubbingEnabled == false && MediaElement.IsPlaying == false)
                            return;

                        SourceBitmap.Render();
                        ApplyScaleTransform(b);
                    }
                    catch (Exception ex)
                    {
                        MediaElement?.MediaCore?.Log(
                            MediaLogMessageType.Error, 
                            $"{nameof(VideoRenderer)} {ex.GetType()}: {ex.Message}. Stack Trace:\r\n{ex.StackTrace}");
                    }
                    finally
                    {
                        IsRenderingInProgress.Value = false;
                    }
                }), block,
                clockPosition);
        }

        /// <summary>
        /// Called on every block rendering clock cycle just in case some update operation needs to be performed.
        /// This needs to return immediately so the calling thread is not disturbed.
        /// </summary>
        /// <param name="clockPosition">The clock position.</param>
        public void Update(TimeSpan clockPosition)
        {
            // placeholder
        }

        #endregion

        #region Helper Methods and IDisposable Support

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Applies the scale transform according to the block's aspect ratio.
        /// </summary>
        /// <param name="b">The b.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyScaleTransform(VideoBlock b)
        {
            var scaleTransform = MediaElement.ViewBox.LayoutTransform as ScaleTransform;

            // Process Aspect Ratio according to block.
            if (b.AspectWidth != b.AspectHeight)
            {
                var scaleX = b.AspectWidth > b.AspectHeight ? (double)b.AspectWidth / b.AspectHeight : 1d;
                var scaleY = b.AspectHeight > b.AspectWidth ? (double)b.AspectHeight / b.AspectWidth : 1d;

                if (scaleTransform == null)
                {
                    scaleTransform = new ScaleTransform(scaleX, scaleY);
                    MediaElement.ViewBox.LayoutTransform = scaleTransform;
                }

                if (scaleTransform.ScaleX != scaleX || scaleTransform.ScaleY != scaleY)
                {
                    scaleTransform.ScaleX = scaleX;
                    scaleTransform.ScaleY = scaleY;
                }
            }
            else
            {
                if (scaleTransform != null && (scaleTransform.ScaleX != 1d || scaleTransform.ScaleY != 1d))
                {
                    scaleTransform.ScaleX = 1d;
                    scaleTransform.ScaleY = 1d;
                }
            }
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        private void Dispose(bool alsoManaged)
        {
            if (!IsDisposed)
            {
                if (alsoManaged && SourceBitmap != null)
                    SourceBitmap.Dispose();

                SourceBitmap = null;
                IsDisposed = true;
            }
        }

        #endregion
    }
}
