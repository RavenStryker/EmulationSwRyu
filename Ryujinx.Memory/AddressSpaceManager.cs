﻿using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Memory
{
    public sealed class AddressSpaceManager : IAddressSpaceManager
    {
        public const int PageBits = 12;
        public const int PageSize = 1 << PageBits;
        public const int PageMask = PageSize - 1;

        private const int PtLevelBits = 9; // 9 * 4 + 12 = 48 (max address space size)
        private const int PtLevelSize = 1 << PtLevelBits;
        private const int PtLevelMask = PtLevelSize - 1;

        private const ulong Unmapped = ulong.MaxValue;

        /// <summary>
        /// Address space width in bits.
        /// </summary>
        public int AddressSpaceBits { get; }

        private readonly ulong _addressSpaceSize;

        private readonly MemoryBlock _backingMemory;

        private readonly ulong[][][][] _pageTable;

        /// <summary>
        /// Creates a new instance of the memory manager.
        /// </summary>
        /// <param name="backingMemory">Physical backing memory where virtual memory will be mapped to</param>
        /// <param name="addressSpaceSize">Size of the address space</param>
        public AddressSpaceManager(MemoryBlock backingMemory, ulong addressSpaceSize)
        {
            ulong asSize = PageSize;
            int asBits = PageBits;

            while (asSize < addressSpaceSize)
            {
                asSize <<= 1;
                asBits++;
            }

            AddressSpaceBits = asBits;
            _addressSpaceSize = asSize;
            _backingMemory = backingMemory;
            _pageTable = new ulong[PtLevelSize][][][];
        }

        /// <summary>
        /// Maps a virtual memory range into a physical memory range.
        /// </summary>
        /// <remarks>
        /// Addresses and size must be page aligned.
        /// </remarks>
        /// <param name="va">Virtual memory address</param>
        /// <param name="pa">Physical memory address</param>
        /// <param name="size">Size to be mapped</param>
        public void Map(ulong va, ulong pa, ulong size)
        {
            while (size != 0)
            {
                PtMap(va, pa);

                va += PageSize;
                pa += PageSize;
                size -= PageSize;
            }
        }

        /// <summary>
        /// Unmaps a previously mapped range of virtual memory.
        /// </summary>
        /// <param name="va">Virtual address of the range to be unmapped</param>
        /// <param name="size">Size of the range to be unmapped</param>
        public void Unmap(ulong va, ulong size)
        {
            while (size != 0)
            {
                PtUnmap(va);

                va += PageSize;
                size -= PageSize;
            }
        }

        /// <summary>
        /// Reads data from CPU mapped memory.
        /// </summary>
        /// <typeparam name="T">Type of the data being read</typeparam>
        /// <param name="va">Virtual address of the data in memory</param>
        /// <returns>The data</returns>
        /// <exception cref="InvalidMemoryRegionException">Throw for unhandled invalid or unmapped memory accesses</exception>
        public T Read<T>(ulong va) where T : unmanaged
        {
            return MemoryMarshal.Cast<byte, T>(GetSpan(va, Unsafe.SizeOf<T>()))[0];
        }

        /// <summary>
        /// Reads data from CPU mapped memory.
        /// </summary>
        /// <param name="va">Virtual address of the data in memory</param>
        /// <param name="data">Span to store the data being read into</param>
        /// <exception cref="InvalidMemoryRegionException">Throw for unhandled invalid or unmapped memory accesses</exception>
        public void Read(ulong va, Span<byte> data)
        {
            ReadImpl(va, data);
        }

        /// <summary>
        /// Writes data to CPU mapped memory.
        /// </summary>
        /// <typeparam name="T">Type of the data being written</typeparam>
        /// <param name="va">Virtual address to write the data into</param>
        /// <param name="value">Data to be written</param>
        /// <exception cref="InvalidMemoryRegionException">Throw for unhandled invalid or unmapped memory accesses</exception>
        public void Write<T>(ulong va, T value) where T : unmanaged
        {
            Write(va, MemoryMarshal.Cast<T, byte>(MemoryMarshal.CreateSpan(ref value, 1)));
        }

        /// <summary>
        /// Writes data to CPU mapped memory.
        /// </summary>
        /// <param name="va">Virtual address to write the data into</param>
        /// <param name="data">Data to be written</param>
        /// <exception cref="InvalidMemoryRegionException">Throw for unhandled invalid or unmapped memory accesses</exception>
        public void Write(ulong va, ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                return;
            }

            if (IsContiguousAndMapped(va, data.Length))
            {
                data.CopyTo(_backingMemory.GetSpan(GetPhysicalAddressInternal(va), data.Length));
            }
            else
            {
                int offset = 0, size;

                if ((va & PageMask) != 0)
                {
                    ulong pa = GetPhysicalAddressInternal(va);

                    size = Math.Min(data.Length, PageSize - (int)(va & PageMask));

                    data.Slice(0, size).CopyTo(_backingMemory.GetSpan(pa, size));

                    offset += size;
                }

                for (; offset < data.Length; offset += size)
                {
                    ulong pa = GetPhysicalAddressInternal(va + (ulong)offset);

                    size = Math.Min(data.Length - offset, PageSize);

                    data.Slice(offset, size).CopyTo(_backingMemory.GetSpan(pa, size));
                }
            }
        }

        /// <summary>
        /// Gets a read-only span of data from CPU mapped memory.
        /// </summary>
        /// <remarks>
        /// This may perform a allocation if the data is not contiguous in memory.
        /// For this reason, the span is read-only, you can't modify the data.
        /// </remarks>
        /// <param name="va">Virtual address of the data</param>
        /// <param name="size">Size of the data</param>
        /// <param name="tracked">True if read tracking is triggered on the span</param>
        /// <returns>A read-only span of the data</returns>
        /// <exception cref="InvalidMemoryRegionException">Throw for unhandled invalid or unmapped memory accesses</exception>
        public ReadOnlySpan<byte> GetSpan(ulong va, int size, bool tracked = false)
        {
            if (size == 0)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            if (IsContiguousAndMapped(va, size))
            {
                return _backingMemory.GetSpan(GetPhysicalAddressInternal(va), size);
            }
            else
            {
                Span<byte> data = new byte[size];

                ReadImpl(va, data);

                return data;
            }
        }

        /// <summary>
        /// Gets a region of memory that can be written to.
        /// </summary>
        /// <remarks>
        /// If the requested region is not contiguous in physical memory,
        /// this will perform an allocation, and flush the data (writing it
        /// back to guest memory) on disposal.
        /// </remarks>
        /// <param name="va">Virtual address of the data</param>
        /// <param name="size">Size of the data</param>
        /// <returns>A writable region of memory containing the data</returns>
        /// <exception cref="InvalidMemoryRegionException">Throw for unhandled invalid or unmapped memory accesses</exception>
        public WritableRegion GetWritableRegion(ulong va, int size)
        {
            if (size == 0)
            {
                return new WritableRegion(null, va, Memory<byte>.Empty);
            }

            if (IsContiguousAndMapped(va, size))
            {
                return new WritableRegion(null, va, _backingMemory.GetMemory(GetPhysicalAddressInternal(va), size));
            }
            else
            {
                Memory<byte> memory = new byte[size];

                GetSpan(va, size).CopyTo(memory.Span);

                return new WritableRegion(this, va, memory);
            }
        }

        /// <summary>
        /// Gets a reference for the given type at the specified virtual memory address.
        /// </summary>
        /// <remarks>
        /// The data must be located at a contiguous memory region.
        /// </remarks>
        /// <typeparam name="T">Type of the data to get the reference</typeparam>
        /// <param name="va">Virtual address of the data</param>
        /// <returns>A reference to the data in memory</returns>
        /// <exception cref="MemoryNotContiguousException">Throw if the specified memory region is not contiguous in physical memory</exception>
        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            if (!IsContiguous(va, Unsafe.SizeOf<T>()))
            {
                ThrowMemoryNotContiguous();
            }

            return ref _backingMemory.GetRef<T>(GetPhysicalAddressInternal(va));
        }

        private void ThrowMemoryNotContiguous() => throw new MemoryNotContiguousException();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsContiguousAndMapped(ulong va, int size) => IsContiguous(va, size) && IsMapped(va);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsContiguous(ulong va, int size)
        {
            if (!ValidateAddress(va))
            {
                return false;
            }

            ulong endVa = (va + (ulong)size + PageMask) & ~(ulong)PageMask;

            va &= ~(ulong)PageMask;

            int pages = (int)((endVa - va) / PageSize);

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    return false;
                }

                if (GetPhysicalAddressInternal(va) + PageSize != GetPhysicalAddressInternal(va + PageSize))
                {
                    return false;
                }

                va += PageSize;
            }

            return true;
        }

        private void ReadImpl(ulong va, Span<byte> data)
        {
            if (data.Length == 0)
            {
                return;
            }

            int offset = 0, size;

            if ((va & PageMask) != 0)
            {
                ulong pa = GetPhysicalAddressInternal(va);

                size = Math.Min(data.Length, PageSize - (int)(va & PageMask));

                _backingMemory.GetSpan(pa, size).CopyTo(data.Slice(0, size));

                offset += size;
            }

            for (; offset < data.Length; offset += size)
            {
                ulong pa = GetPhysicalAddressInternal(va + (ulong)offset);

                size = Math.Min(data.Length - offset, PageSize);

                _backingMemory.GetSpan(pa, size).CopyTo(data.Slice(offset, size));
            }
        }

        /// <summary>
        /// Checks if the page at a given CPU virtual address.
        /// </summary>
        /// <param name="va">Virtual address to check</param>
        /// <returns>True if the address is mapped, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsMapped(ulong va)
        {
            if (!ValidateAddress(va))
            {
                return false;
            }

            return PtRead(va) != Unmapped;
        }

        private bool ValidateAddress(ulong va)
        {
            return va < _addressSpaceSize;
        }

        /// <summary>
        /// Performs address translation of the address inside a CPU mapped memory range.
        /// </summary>
        /// <remarks>
        /// If the address is invalid or unmapped, -1 will be returned.
        /// </remarks>
        /// <param name="va">Virtual address to be translated</param>
        /// <returns>The physical address</returns>
        public ulong GetPhysicalAddress(ulong va)
        {
            // We return -1L if the virtual address is invalid or unmapped.
            if (!ValidateAddress(va) || !IsMapped(va))
            {
                return ulong.MaxValue;
            }

            return GetPhysicalAddressInternal(va);
        }

        private ulong GetPhysicalAddressInternal(ulong va)
        {
            return PtRead(va) + (va & PageMask);
        }

        private ulong PtRead(ulong va)
        {
            int l3 = (int)(va >> PageBits) & PtLevelMask;
            int l2 = (int)(va >> (PageBits + PtLevelBits)) & PtLevelMask;
            int l1 = (int)(va >> (PageBits + PtLevelBits * 2)) & PtLevelMask;
            int l0 = (int)(va >> (PageBits + PtLevelBits * 3)) & PtLevelMask;

            if (_pageTable[l0] == null)
            {
                return Unmapped;
            }

            if (_pageTable[l0][l1] == null)
            {
                return Unmapped;
            }

            if (_pageTable[l0][l1][l2] == null)
            {
                return Unmapped;
            }

            return _pageTable[l0][l1][l2][l3];
        }

        private void PtMap(ulong va, ulong value)
        {
            int l3 = (int)(va >> PageBits) & PtLevelMask;
            int l2 = (int)(va >> (PageBits + PtLevelBits)) & PtLevelMask;
            int l1 = (int)(va >> (PageBits + PtLevelBits * 2)) & PtLevelMask;
            int l0 = (int)(va >> (PageBits + PtLevelBits * 3)) & PtLevelMask;

            if (_pageTable[l0] == null)
            {
                _pageTable[l0] = new ulong[PtLevelSize][][];
            }

            if (_pageTable[l0][l1] == null)
            {
                _pageTable[l0][l1] = new ulong[PtLevelSize][];
            }

            if (_pageTable[l0][l1][l2] == null)
            {
                _pageTable[l0][l1][l2] = new ulong[PtLevelSize];

                for (int i = 0; i < _pageTable[l0][l1][l2].Length; i++)
                {
                    _pageTable[l0][l1][l2][i] = Unmapped;
                }
            }

            _pageTable[l0][l1][l2][l3] = value;
        }

        private void PtUnmap(ulong va)
        {
            int l3 = (int)(va >> PageBits) & PtLevelMask;
            int l2 = (int)(va >> (PageBits + PtLevelBits)) & PtLevelMask;
            int l1 = (int)(va >> (PageBits + PtLevelBits * 2)) & PtLevelMask;
            int l0 = (int)(va >> (PageBits + PtLevelBits * 3)) & PtLevelMask;

            if (_pageTable[l0] == null)
            {
                return;
            }

            if (_pageTable[l0][l1] == null)
            {
                return;
            }

            if (_pageTable[l0][l1][l2] == null)
            {
                return;
            }

            _pageTable[l0][l1][l2][l3] = Unmapped;

            bool empty = true;

            for (int i = 0; i < _pageTable[l0][l1][l2].Length; i++)
            {
                empty &= (_pageTable[l0][l1][l2][i] == Unmapped);
            }

            if (empty)
            {
                _pageTable[l0][l1][l2] = null;

                RemoveIfAllNull(l0, l1);
            }
        }

        private void RemoveIfAllNull(int l0, int l1)
        {
            bool empty = true;

            for (int i = 0; i < _pageTable[l0][l1].Length; i++)
            {
                empty &= (_pageTable[l0][l1][i] == null);
            }

            if (empty)
            {
                _pageTable[l0][l1] = null;

                RemoveIfAllNull(l0);
            }
        }

        private void RemoveIfAllNull(int l0)
        {
            bool empty = true;

            for (int i = 0; i < _pageTable[l0].Length; i++)
            {
                empty &= (_pageTable[l0][i] == null);
            }

            if (empty)
            {
                _pageTable[l0] = null;
            }
        }
    }
}
