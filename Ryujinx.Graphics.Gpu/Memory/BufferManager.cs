﻿using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Ryujinx.Graphics.Gpu.Memory
{
    class BufferManager : IDisposable
    {
        private const int StackToHeapThreshold = 16;

        private readonly GpuContext _context;

        private IndexBuffer _indexBuffer;
        private readonly VertexBuffer[] _vertexBuffers;
        private readonly BufferBounds[] _transformFeedbackBuffers;
        private readonly List<BufferTextureBinding> _bufferTextures;

        /// <summary>
        /// Holds shader stage buffer state and binding information.
        /// </summary>
        private class BuffersPerStage
        {
            /// <summary>
            /// Shader buffer binding information.
            /// </summary>
            public BufferDescriptor[] Bindings { get; }

            /// <summary>
            /// Buffer regions.
            /// </summary>
            public BufferBounds[] Buffers { get; }

            /// <summary>
            /// Total amount of buffers used on the shader.
            /// </summary>
            public int Count { get; private set; }

            /// <summary>
            /// Creates a new instance of the shader stage buffer information.
            /// </summary>
            /// <param name="count">Maximum amount of buffers that the shader stage can use</param>
            public BuffersPerStage(int count)
            {
                Bindings = new BufferDescriptor[count];
                Buffers = new BufferBounds[count];
            }

            /// <summary>
            /// Sets the region of a buffer at a given slot.
            /// </summary>
            /// <param name="index">Buffer slot</param>
            /// <param name="address">Region virtual address</param>
            /// <param name="size">Region size in bytes</param>
            /// <param name="flags">Buffer usage flags</param>
            public void SetBounds(int index, ulong address, ulong size, BufferUsageFlags flags = BufferUsageFlags.None)
            {
                Buffers[index] = new BufferBounds(address, size, flags);
            }

            /// <summary>
            /// Sets shader buffer binding information.
            /// </summary>
            /// <param name="descriptors">Buffer binding information</param>
            public void SetBindings(ReadOnlyCollection<BufferDescriptor> descriptors)
            {
                if (descriptors == null)
                {
                    Count = 0;
                    return;
                }

                descriptors.CopyTo(Bindings, 0);
                Count = descriptors.Count;
            }
        }

        private readonly BuffersPerStage _cpStorageBuffers;
        private readonly BuffersPerStage _cpUniformBuffers;
        private readonly BuffersPerStage[] _gpStorageBuffers;
        private readonly BuffersPerStage[] _gpUniformBuffers;

        private int _cpStorageBufferBindings;
        private int _cpUniformBufferBindings;
        private int _gpStorageBufferBindings;
        private int _gpUniformBufferBindings;

        private bool _gpStorageBuffersDirty;
        private bool _gpUniformBuffersDirty;

        private bool _indexBufferDirty;
        private bool _vertexBuffersDirty;
        private uint _vertexBuffersEnableMask;
        private bool _transformFeedbackBuffersDirty;

        private bool _rebind;

        public BufferManager(GpuContext context)
        {
            _context = context;

            _vertexBuffers = new VertexBuffer[Constants.TotalVertexBuffers];

            _transformFeedbackBuffers = new BufferBounds[Constants.TotalTransformFeedbackBuffers];

            _cpStorageBuffers = new BuffersPerStage(Constants.TotalCpStorageBuffers);
            _cpUniformBuffers = new BuffersPerStage(Constants.TotalCpUniformBuffers);

            _gpStorageBuffers = new BuffersPerStage[Constants.ShaderStages];
            _gpUniformBuffers = new BuffersPerStage[Constants.ShaderStages];

            for (int index = 0; index < Constants.ShaderStages; index++)
            {
                _gpStorageBuffers[index] = new BuffersPerStage(Constants.TotalGpStorageBuffers);
                _gpUniformBuffers[index] = new BuffersPerStage(Constants.TotalGpUniformBuffers);
            }

            _bufferTextures = new List<BufferTextureBinding>();

            context.Methods.BufferCache.NotifyBuffersModified += BuffersModified;
        }


        /// <summary>
        /// Sets the memory range with the index buffer data, to be used for subsequent draw calls.
        /// </summary>
        /// <param name="gpuVa">Start GPU virtual address of the index buffer</param>
        /// <param name="size">Size, in bytes, of the index buffer</param>
        /// <param name="type">Type of each index buffer element</param>
        public void SetIndexBuffer(ulong gpuVa, ulong size, IndexType type)
        {
            ulong address = _context.Methods.BufferCache.TranslateAndCreateBuffer(gpuVa, size);

            _indexBuffer.Address = address;
            _indexBuffer.Size = size;
            _indexBuffer.Type = type;

            _indexBufferDirty = true;
        }

        /// <summary>
        /// Sets a new index buffer that overrides the one set on the call to <see cref="CommitGraphicsBindings"/>.
        /// </summary>
        /// <param name="buffer">Buffer to be used as index buffer</param>
        /// <param name="type">Type of each index buffer element</param>
        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _context.Renderer.Pipeline.SetIndexBuffer(buffer, type);

            _indexBufferDirty = true;
        }

        /// <summary>
        /// Sets the memory range with vertex buffer data, to be used for subsequent draw calls.
        /// </summary>
        /// <param name="index">Index of the vertex buffer (up to 16)</param>
        /// <param name="gpuVa">GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stride">Stride of the buffer, defined as the number of bytes of each vertex</param>
        /// <param name="divisor">Vertex divisor of the buffer, for instanced draws</param>
        public void SetVertexBuffer(int index, ulong gpuVa, ulong size, int stride, int divisor)
        {
            ulong address = _context.Methods.BufferCache.TranslateAndCreateBuffer(gpuVa, size);

            _vertexBuffers[index].Address = address;
            _vertexBuffers[index].Size = size;
            _vertexBuffers[index].Stride = stride;
            _vertexBuffers[index].Divisor = divisor;

            _vertexBuffersDirty = true;

            if (address != 0)
            {
                _vertexBuffersEnableMask |= 1u << index;
            }
            else
            {
                _vertexBuffersEnableMask &= ~(1u << index);
            }
        }

        /// <summary>
        /// Sets a transform feedback buffer on the graphics pipeline.
        /// The output from the vertex transformation stages are written into the feedback buffer.
        /// </summary>
        /// <param name="index">Index of the transform feedback buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the transform feedback buffer</param>
        public void SetTransformFeedbackBuffer(int index, ulong gpuVa, ulong size)
        {
            ulong address = _context.Methods.BufferCache.TranslateAndCreateBuffer(gpuVa, size);

            _transformFeedbackBuffers[index] = new BufferBounds(address, size);
            _transformFeedbackBuffersDirty = true;
        }

        /// <summary>
        /// Sets a storage buffer on the compute pipeline.
        /// Storage buffers can be read and written to on shaders.
        /// </summary>
        /// <param name="index">Index of the storage buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        /// <param name="flags">Buffer usage flags</param>
        public void SetComputeStorageBuffer(int index, ulong gpuVa, ulong size, BufferUsageFlags flags)
        {
            size += gpuVa & ((ulong)_context.Capabilities.StorageBufferOffsetAlignment - 1);

            gpuVa = BitUtils.AlignDown(gpuVa, _context.Capabilities.StorageBufferOffsetAlignment);

            ulong address = _context.Methods.BufferCache.TranslateAndCreateBuffer(gpuVa, size);

            _cpStorageBuffers.SetBounds(index, address, size, flags);
        }

        /// <summary>
        /// Sets a storage buffer on the graphics pipeline.
        /// Storage buffers can be read and written to on shaders.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the storage buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        /// <param name="flags">Buffer usage flags</param>
        public void SetGraphicsStorageBuffer(int stage, int index, ulong gpuVa, ulong size, BufferUsageFlags flags)
        {
            size += gpuVa & ((ulong)_context.Capabilities.StorageBufferOffsetAlignment - 1);

            gpuVa = BitUtils.AlignDown(gpuVa, _context.Capabilities.StorageBufferOffsetAlignment);

            ulong address = _context.Methods.BufferCache.TranslateAndCreateBuffer(gpuVa, size);

            if (_gpStorageBuffers[stage].Buffers[index].Address != address ||
                _gpStorageBuffers[stage].Buffers[index].Size != size)
            {
                _gpStorageBuffersDirty = true;
            }

            _gpStorageBuffers[stage].SetBounds(index, address, size, flags);
        }

        /// <summary>
        /// Sets a uniform buffer on the compute pipeline.
        /// Uniform buffers are read-only from shaders, and have a small capacity.
        /// </summary>
        /// <param name="index">Index of the uniform buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        public void SetComputeUniformBuffer(int index, ulong gpuVa, ulong size)
        {
            ulong address = _context.Methods.BufferCache.TranslateAndCreateBuffer(gpuVa, size);

            _cpUniformBuffers.SetBounds(index, address, size);
        }

        /// <summary>
        /// Sets a uniform buffer on the graphics pipeline.
        /// Uniform buffers are read-only from shaders, and have a small capacity.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the uniform buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        public void SetGraphicsUniformBuffer(int stage, int index, ulong gpuVa, ulong size)
        {
            ulong address = _context.Methods.BufferCache.TranslateAndCreateBuffer(gpuVa, size);

            _gpUniformBuffers[stage].SetBounds(index, address, size);
            _gpUniformBuffersDirty = true;
        }

        /// <summary>
        /// Sets the binding points for the storage buffers bound on the compute pipeline.
        /// </summary>
        /// <param name="descriptors">Buffer descriptors with the binding point values</param>
        public void SetComputeStorageBufferBindings(ReadOnlyCollection<BufferDescriptor> descriptors)
        {
            _cpStorageBuffers.SetBindings(descriptors);
            _cpStorageBufferBindings = descriptors.Count != 0 ? descriptors.Max(x => x.Binding) + 1 : 0;
        }

        /// <summary>
        /// Sets the binding points for the storage buffers bound on the graphics pipeline.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="descriptors">Buffer descriptors with the binding point values</param>
        public void SetGraphicsStorageBufferBindings(int stage, ReadOnlyCollection<BufferDescriptor> descriptors)
        {
            _gpStorageBuffers[stage].SetBindings(descriptors);
            _gpStorageBuffersDirty = true;
        }

        /// <summary>
        /// Sets the total number of storage buffer bindings used.
        /// </summary>
        /// <param name="count">Number of storage buffer bindings used</param>
        public void SetGraphicsStorageBufferBindingsCount(int count)
        {
            _gpStorageBufferBindings = count;
        }

        /// <summary>
        /// Sets the binding points for the uniform buffers bound on the compute pipeline.
        /// </summary>
        /// <param name="descriptors">Buffer descriptors with the binding point values</param>
        public void SetComputeUniformBufferBindings(ReadOnlyCollection<BufferDescriptor> descriptors)
        {
            _cpUniformBuffers.SetBindings(descriptors);
            _cpUniformBufferBindings = descriptors.Count != 0 ? descriptors.Max(x => x.Binding) + 1 : 0;
        }

        /// <summary>
        /// Sets the enabled uniform buffers mask on the graphics pipeline.
        /// Each bit set on the mask indicates that the respective buffer index is enabled.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="descriptors">Buffer descriptors with the binding point values</param>
        public void SetGraphicsUniformBufferBindings(int stage, ReadOnlyCollection<BufferDescriptor> descriptors)
        {
            _gpUniformBuffers[stage].SetBindings(descriptors);
            _gpUniformBuffersDirty = true;
        }

        /// <summary>
        /// Sets the total number of uniform buffer bindings used.
        /// </summary>
        /// <param name="count">Number of uniform buffer bindings used</param>
        public void SetGraphicsUniformBufferBindingsCount(int count)
        {
            _gpUniformBufferBindings = count;
        }

        /// <summary>
        /// Gets a bit mask indicating which compute uniform buffers are currently bound.
        /// </summary>
        /// <returns>Mask where each bit set indicates a bound constant buffer</returns>
        public uint GetComputeUniformBufferUseMask()
        {
            uint mask = 0;

            for (int i = 0; i < _cpUniformBuffers.Buffers.Length; i++)
            {
                if (_cpUniformBuffers.Buffers[i].Address != 0)
                {
                    mask |= 1u << i;
                }
            }

            return mask;
        }

        /// <summary>
        /// Gets a bit mask indicating which graphics uniform buffers are currently bound.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <returns>Mask where each bit set indicates a bound constant buffer</returns>
        public uint GetGraphicsUniformBufferUseMask(int stage)
        {
            uint mask = 0;

            for (int i = 0; i < _gpUniformBuffers[stage].Buffers.Length; i++)
            {
                if (_gpUniformBuffers[stage].Buffers[i].Address != 0)
                {
                    mask |= 1u << i;
                }
            }

            return mask;
        }


        /// <summary>
        /// Gets the address of the compute uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer address, or an undefined value if the buffer is not currently bound</returns>
        public ulong GetComputeUniformBufferAddress(int index)
        {
            return _cpUniformBuffers.Buffers[index].Address;
        }

        /// <summary>
        /// Gets the address of the graphics uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer address, or an undefined value if the buffer is not currently bound</returns>
        public ulong GetGraphicsUniformBufferAddress(int stage, int index)
        {
            return _gpUniformBuffers[stage].Buffers[index].Address;
        }

        /// <summary>
        /// Ensures that the compute engine bindings are visible to the host GPU.
        /// Note: this actually performs the binding using the host graphics API.
        /// </summary>
        public void CommitComputeBindings()
        {
            int sCount = _cpStorageBufferBindings;

            Span<BufferRange> sRanges = sCount < StackToHeapThreshold ? stackalloc BufferRange[sCount] : new BufferRange[sCount];

            for (int index = 0; index < _cpStorageBuffers.Count; index++)
            {
                ref var bindingInfo = ref _cpStorageBuffers.Bindings[index];

                BufferBounds bounds = _cpStorageBuffers.Buffers[bindingInfo.Slot];

                if (bounds.Address != 0)
                {
                    // The storage buffer size is not reliable (it might be lower than the actual size),
                    // so we bind the entire buffer to allow otherwise out of range accesses to work.
                    sRanges[bindingInfo.Binding] = _context.Methods.BufferCache.GetBufferRangeTillEnd(
                        bounds.Address,
                        bounds.Size,
                        bounds.Flags.HasFlag(BufferUsageFlags.Write));
                }
            }

            _context.Renderer.Pipeline.SetStorageBuffers(sRanges);

            int uCount = _cpUniformBufferBindings;

            Span<BufferRange> uRanges = uCount < StackToHeapThreshold ? stackalloc BufferRange[uCount] : new BufferRange[uCount];

            for (int index = 0; index < _cpUniformBuffers.Count; index++)
            {
                ref var bindingInfo = ref _cpUniformBuffers.Bindings[index];

                BufferBounds bounds = _cpUniformBuffers.Buffers[bindingInfo.Slot];

                if (bounds.Address != 0)
                {
                    uRanges[bindingInfo.Binding] = _context.Methods.BufferCache.GetBufferRange(bounds.Address, bounds.Size);
                }
            }

            _context.Renderer.Pipeline.SetUniformBuffers(uRanges);

            CommitBufferTextureBindings();

            // Force rebind after doing compute work.
            _rebind = true;
        }

        /// <summary>
        /// Commit any queued buffer texture bindings.
        /// </summary>
        private void CommitBufferTextureBindings()
        {
            if (_bufferTextures.Count > 0)
            {
                foreach (var binding in _bufferTextures)
                {
                    var isStore = binding.BindingInfo.Flags.HasFlag(TextureUsageFlags.ImageStore);
                    var range = _context.Methods.BufferCache.GetBufferRange(binding.Address, binding.Size, isStore);
                    binding.Texture.SetStorage(range);

                    // The texture must be rebound to use the new storage if it was updated.

                    if (binding.IsImage)
                    {
                        _context.Renderer.Pipeline.SetImage(binding.BindingInfo.Binding, binding.Texture, binding.Format);
                    }
                    else
                    {
                        _context.Renderer.Pipeline.SetTexture(binding.BindingInfo.Binding, binding.Texture);
                    }
                }

                _bufferTextures.Clear();
            }
        }

        /// <summary>
        /// Ensures that the graphics engine bindings are visible to the host GPU.
        /// Note: this actually performs the binding using the host graphics API.
        /// </summary>
        public void CommitGraphicsBindings()
        {
            if (_indexBufferDirty || _rebind)
            {
                _indexBufferDirty = false;

                if (_indexBuffer.Address != 0)
                {
                    BufferRange buffer = _context.Methods.BufferCache.GetBufferRange(_indexBuffer.Address, _indexBuffer.Size);

                    _context.Renderer.Pipeline.SetIndexBuffer(buffer, _indexBuffer.Type);
                }
            }
            else if (_indexBuffer.Address != 0)
            {
                _context.Methods.BufferCache.SynchronizeBufferRange(_indexBuffer.Address, _indexBuffer.Size);
            }

            uint vbEnableMask = _vertexBuffersEnableMask;

            if (_vertexBuffersDirty || _rebind)
            {
                _vertexBuffersDirty = false;

                Span<VertexBufferDescriptor> vertexBuffers = stackalloc VertexBufferDescriptor[Constants.TotalVertexBuffers];

                for (int index = 0; (vbEnableMask >> index) != 0; index++)
                {
                    VertexBuffer vb = _vertexBuffers[index];

                    if (vb.Address == 0)
                    {
                        continue;
                    }

                    BufferRange buffer = _context.Methods.BufferCache.GetBufferRange(vb.Address, vb.Size);

                    vertexBuffers[index] = new VertexBufferDescriptor(buffer, vb.Stride, vb.Divisor);
                }

                _context.Renderer.Pipeline.SetVertexBuffers(vertexBuffers);
            }
            else
            {
                for (int index = 0; (vbEnableMask >> index) != 0; index++)
                {
                    VertexBuffer vb = _vertexBuffers[index];

                    if (vb.Address == 0)
                    {
                        continue;
                    }

                    _context.Methods.BufferCache.SynchronizeBufferRange(vb.Address, vb.Size);
                }
            }

            if (_transformFeedbackBuffersDirty || _rebind)
            {
                _transformFeedbackBuffersDirty = false;

                Span<BufferRange> tfbs = stackalloc BufferRange[Constants.TotalTransformFeedbackBuffers];

                for (int index = 0; index < Constants.TotalTransformFeedbackBuffers; index++)
                {
                    BufferBounds tfb = _transformFeedbackBuffers[index];

                    if (tfb.Address == 0)
                    {
                        tfbs[index] = BufferRange.Empty;
                        continue;
                    }

                    tfbs[index] = _context.Methods.BufferCache.GetBufferRange(tfb.Address, tfb.Size);
                }

                _context.Renderer.Pipeline.SetTransformFeedbackBuffers(tfbs);
            }
            else
            {
                for (int index = 0; index < Constants.TotalTransformFeedbackBuffers; index++)
                {
                    BufferBounds tfb = _transformFeedbackBuffers[index];

                    if (tfb.Address == 0)
                    {
                        continue;
                    }

                    _context.Methods.BufferCache.SynchronizeBufferRange(tfb.Address, tfb.Size);
                }
            }

            if (_gpStorageBuffersDirty || _rebind)
            {
                _gpStorageBuffersDirty = false;

                BindBuffers(_gpStorageBuffers, isStorage: true);
            }
            else
            {
                UpdateBuffers(_gpStorageBuffers);
            }

            if (_gpUniformBuffersDirty || _rebind)
            {
                _gpUniformBuffersDirty = false;

                BindBuffers(_gpUniformBuffers, isStorage: false);
            }
            else
            {
                UpdateBuffers(_gpUniformBuffers);
            }

            CommitBufferTextureBindings();

            _rebind = false;
        }

        /// <summary>
        /// Bind respective buffer bindings on the host API.
        /// </summary>
        /// <param name="bindings">Bindings to bind</param>
        /// <param name="isStorage">True to bind as storage buffer, false to bind as uniform buffers</param>
        private void BindBuffers(BuffersPerStage[] bindings, bool isStorage)
        {
            int count = isStorage ? _gpStorageBufferBindings : _gpUniformBufferBindings;

            Span<BufferRange> ranges = count < StackToHeapThreshold ? stackalloc BufferRange[count] : new BufferRange[count];

            for (ShaderStage stage = ShaderStage.Vertex; stage <= ShaderStage.Fragment; stage++)
            {
                ref var buffers = ref bindings[(int)stage - 1];

                for (int index = 0; index < buffers.Count; index++)
                {
                    ref var bindingInfo = ref buffers.Bindings[index];

                    BufferBounds bounds = buffers.Buffers[bindingInfo.Slot];

                    if (bounds.Address != 0)
                    {
                        var isWrite = bounds.Flags.HasFlag(BufferUsageFlags.Write);
                        ranges[bindingInfo.Binding] = isStorage
                            ? _context.Methods.BufferCache.GetBufferRangeTillEnd(bounds.Address, bounds.Size, isWrite)
                            : _context.Methods.BufferCache.GetBufferRange(bounds.Address, bounds.Size, isWrite);
                    }
                }
            }

            if (isStorage)
            {
                _context.Renderer.Pipeline.SetStorageBuffers(ranges);
            }
            else
            {
                _context.Renderer.Pipeline.SetUniformBuffers(ranges);
            }
        }

        /// <summary>
        /// Updates data for the already bound buffer bindings.
        /// </summary>
        /// <param name="bindings">Bindings to update</param>
        private void UpdateBuffers(BuffersPerStage[] bindings)
        {
            for (ShaderStage stage = ShaderStage.Vertex; stage <= ShaderStage.Fragment; stage++)
            {
                ref var buffers = ref bindings[(int)stage - 1];

                for (int index = 0; index < buffers.Count; index++)
                {
                    ref var binding = ref buffers.Bindings[index];

                    BufferBounds bounds = buffers.Buffers[binding.Slot];

                    if (bounds.Address == 0)
                    {
                        continue;
                    }

                    _context.Methods.BufferCache.SynchronizeBufferRange(bounds.Address, bounds.Size);
                }
            }
        }

        /// <summary>
        /// Sets the buffer storage of a buffer texture. This will be bound when the buffer manager commits bindings.
        /// </summary>
        /// <param name="texture">Buffer texture</param>
        /// <param name="address">Address of the buffer in memory</param>
        /// <param name="size">Size of the buffer in bytes</param>
        /// <param name="bindingInfo">Binding info for the buffer texture</param>
        /// <param name="format">Format of the buffer texture</param>
        /// <param name="isImage">Whether the binding is for an image or a sampler</param>
        public void SetBufferTextureStorage(ITexture texture, ulong address, ulong size, TextureBindingInfo bindingInfo, Format format, bool isImage)
        {
            _context.Methods.BufferCache.CreateBuffer(address, size);

            _bufferTextures.Add(new BufferTextureBinding(texture, address, size, bindingInfo, format, isImage));
        }

        /// <summary>
        /// Requests all buffers to be rebound to account for possible re-creation of buffers currently bound.
        /// </summary>
        private void BuffersModified()
        {
            _rebind = true;
        }

        /// <summary>
        /// Disposes the buffer manager.
        /// It is an error to use the buffer manager after disposal.
        /// </summary>
        public void Dispose()
        {
            _context.Methods.BufferCache.NotifyBuffersModified -= BuffersModified;
        }
    }
}
