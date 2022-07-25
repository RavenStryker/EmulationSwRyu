using System;
using System.Diagnostics;

namespace Ryujinx.Audio.Renderer.Server.Upsampler
{
    /// <summary>
    /// Upsampler manager.
    /// </summary>
    public class UpsamplerManager
    {
        /// <summary>
        /// Work buffer for upsampler.
        /// </summary>
        private Memory<float> _upSamplerWorkBuffer;

        /// <summary>
        /// Global lock of the object.
        /// </summary>
        private object Lock = new object();

        /// <summary>
        /// The upsamplers instances.
        /// </summary>
        private UpsamplerState[] _upsamplers;

        /// <summary>
        /// The count of upsamplers.
        /// </summary>
        private uint _count;

        /// <summary>
        /// Create a new <see cref="UpsamplerManager"/>.
        /// </summary>
        /// <param name="upSamplerWorkBuffer">Work buffer for upsampler.</param>
        /// <param name="count">The count of upsamplers.</param>
        public UpsamplerManager(Memory<float> upSamplerWorkBuffer, uint count)
        {
            _upSamplerWorkBuffer = upSamplerWorkBuffer;
            _count = count;

            _upsamplers = new UpsamplerState[_count];
        }

        /// <summary>
        /// Allocate a new <see cref="UpsamplerState"/>.
        /// </summary>
        /// <returns>A new <see cref="UpsamplerState"/> or null if out of memory.</returns>
        public UpsamplerState Allocate()
        {
            int workBufferOffset = 0;

            lock (Lock)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_upsamplers[i] == null)
                    {
                        _upsamplers[i] = new UpsamplerState(this, i, _upSamplerWorkBuffer.Slice(workBufferOffset, Constants.UpSampleEntrySize), Constants.TargetSampleCount);

                        return _upsamplers[i];
                    }

                    workBufferOffset += Constants.UpSampleEntrySize;
                }
            }

            return null;
        }

        /// <summary>
        /// Free a <see cref="UpsamplerState"/> at the given index.
        /// </summary>
        /// <param name="index">The index of the <see cref="UpsamplerState"/> to free.</param>
        public void Free(int index)
        {
            lock (Lock)
            {
                Debug.Assert(_upsamplers[index] != null);

                _upsamplers[index] = null;
            }
        }
    }
}