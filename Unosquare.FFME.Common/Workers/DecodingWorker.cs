namespace Unosquare.FFME.Workers
{
    using FFME.Shared;
    using Primitives;
    using System;
    using System.Runtime.CompilerServices;

    internal sealed class DecodingWorker : MediaWorker
    {
        private const int CycleMilliseconds = 20;
        private const double RangePercentThreshold = 0.75d;
        private MediaBlockBuffer blocks;
        private MediaType main;
        private long decodingBirtrate;
        private int decodedFrameCount;
        private double rangePercent;
        private TimeSpan wallClock;

        public DecodingWorker(MediaEngine mediaCore)
            : base(nameof(RenderingWorker), mediaCore)
        {
        }

        protected override void ExecuteWorkerCycle(out bool cycle)
        {
            cycle = false;
            Commands.ExecuteNextQueuedCommand();
            if (MediaCore.HasDecodingEnded) return;

            decodedFrameCount = 0;
            decodingBirtrate = 0;
            main = Components.MainMediaType;
            wallClock = MediaCore.WallClock;

            // We need to add blocks if the wall clock is over 75%
            // for each of the components so that we have some buffer.
            foreach (var t in Container.Components.MediaTypes)
            {
                if (IsWorkerInterruptRequested) break;

                // Capture a reference to the blocks and the current Range Percent
                blocks = Blocks[t];
                rangePercent = blocks.GetRangePercent(wallClock);
                decodingBirtrate += blocks.RangeBitRate;

                // Read as much as we can for this cycle but always within range.
                while (blocks.IsFull == false || rangePercent > RangePercentThreshold)
                {
                    // Stop decoding blocks
                    if (IsWorkerInterruptRequested)
                        break;

                    if (Components[t].BufferLength <= 0 && Components[t].HasPacketsInCodec == false)
                        break;

                    if (!AddNextBlock(t))
                        break;

                    decodedFrameCount += 1;
                    rangePercent = blocks.GetRangePercent(wallClock);

                    // Determine break conditions to save CPU time
                    if (rangePercent > 0 &&
                        rangePercent <= RangePercentThreshold &&
                        blocks.IsFull == false &&
                        blocks.CapacityPercent >= 0.25d &&
                        blocks.IsInRange(wallClock))
                        break;
                }
            }

            // Main block timing check
            blocks = Blocks[main];

            // Unfortunately at this point we will need to adjust the clock after creating the frames.
            // to ensure tha main component is within the clock range if the decoded
            // frames are not with range. This is normal while buffering though.
            if (blocks.IsInRange(wallClock) == false)
            {
                // Update the wall clock to the most appropriate available block.
                if (blocks.Count > 0)
                    MediaCore.ChangePosition(blocks[wallClock].StartTime);
                else
                    MediaCore.Clock.Pause();
            }

            // Provide updates to decoding stats
            if (decodedFrameCount > 0)
                State.UpdateDecodingBitRate(decodingBirtrate);

            // Detect End of Decoding Scenarios
            // The Rendering will check for end of media when this
            // condition is set.
            MediaCore.HasDecodingEnded = decodedFrameCount <= 0
                && IsWorkerInterruptRequested == false
                && MediaCore.CanReadMoreFramesOf(main) == false
                && blocks.IndexOf(wallClock) >= blocks.Count - 1;

            cycle = decodedFrameCount > 0;
        }

        /// <summary>
        /// Tries to receive the next frame from the decoder by decoding queued
        /// Packets and converting the decoded frame into a Media Block which gets
        /// queued into the playback block buffer.
        /// </summary>
        /// <param name="t">The MediaType.</param>
        /// <returns>True if a block could be added. False otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool AddNextBlock(MediaType t)
        {
            // Decode the frames
            var block = Blocks[t].Add(Container.Components[t].ReceiveNextFrame(), Container);
            return block != null;
        }
    }
}
