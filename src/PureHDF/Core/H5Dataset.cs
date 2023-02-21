using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PureHDF
{
    [DebuggerDisplay("{Name}: Class = '{InternalDataType.Class}'")]
    partial class H5Dataset : H5AttributableObject
    {
        #region Fields

        private H5Dataspace? _space;
        private H5DataType? _type;
        private H5DataLayout? _layout;
        private H5FillValue? _fillValue;

        #endregion

        #region Constructors

        internal H5Dataset(H5File file, H5Context context, NamedReference reference, ObjectHeader header)
            : base(context, reference, header)
        {
            File = file;

            foreach (var message in Header.HeaderMessages)
            {
                var type = message.Data.GetType();

                if (typeof(DataLayoutMessage).IsAssignableFrom(type))
                    InternalDataLayout = (DataLayoutMessage)message.Data;

                else if (type == typeof(DataspaceMessage))
                    InternalDataspace = (DataspaceMessage)message.Data;

                else if (type == typeof(DatatypeMessage))
                    InternalDataType = (DatatypeMessage)message.Data;

                else if (type == typeof(FillValueMessage))
                    InternalFillValue = (FillValueMessage)message.Data;

                else if (type == typeof(FilterPipelineMessage))
                    InternalFilterPipeline = (FilterPipelineMessage)message.Data;

                else if (type == typeof(ObjectModificationMessage))
                    InternalObjectModification = (ObjectModificationMessage)message.Data;

                else if (type == typeof(ExternalFileListMessage))
                    InternalExternalFileList = (ExternalFileListMessage)message.Data;
            }

            // check that required fields are set
            if (InternalDataLayout is null)
                throw new Exception("The data layout message is missing.");

            if (InternalDataspace is null)
                throw new Exception("The dataspace message is missing.");

            if (InternalDataType is null)
                throw new Exception("The data type message is missing.");

            // https://github.com/Apollo3zehn/PureHDF/issues/25
            if (InternalFillValue is null)
            {
                // The OldFillValueMessage is optional and so there might be no fill value
                // message at all although the newer message is being marked as required. The
                // workaround is to instantiate a new FillValueMessage with sensible defaults.
                // It is not 100% clear if these defaults are fine.

                var allocationTime = InternalDataLayout.LayoutClass == LayoutClass.Chunked
                    ? SpaceAllocationTime.Incremental
                    : SpaceAllocationTime.Late;

                InternalFillValue = new FillValueMessage(allocationTime);
            }
        }

        #endregion

        #region Properties

        internal DataLayoutMessage InternalDataLayout { get; } = default!;

        internal DataspaceMessage InternalDataspace { get; } = default!;

        internal DatatypeMessage InternalDataType { get; } = default!;

        internal FillValueMessage InternalFillValue { get; } = default!;

        internal FilterPipelineMessage? InternalFilterPipeline { get; }

        internal ObjectModificationMessage? InternalObjectModification { get; }

        internal ExternalFileListMessage? InternalExternalFileList { get; }

        #endregion

        #region Private

        internal async Task<TResult[]?> ReadCoreValueAsync<TResult, TReader>(
            TReader reader,
            Memory<TResult> destination,
            Selection? fileSelection = default,
            Selection? memorySelection = default,
            ulong[]? memoryDims = default,
            H5DatasetAccess datasetAccess = default,
            bool skipShuffle = false)
                where TResult : unmanaged
                where TReader : IReader
        {
            // only allow size of T that matches bytesOfType or size of T = 1
            var sizeOfT = (ulong)Unsafe.SizeOf<TResult>();
            var bytesOfType = InternalDataType.Size;

            if (bytesOfType % sizeOfT != 0)
                throw new Exception("The size of the generic parameter must be a multiple of the HDF5 file internal data type size.");

            var factor = (int)(bytesOfType / sizeOfT);

            static void converter(Memory<byte> source, Memory<TResult> target) 
                => source.Span.CopyTo(MemoryMarshal.AsBytes(target.Span));

            Task readVirtualDelegate(H5Dataset dataset, Memory<TResult> destination, Selection fileSelection, H5DatasetAccess datasetAccess)
                => dataset.ReadCoreValueAsync(
                    reader, 
                    destination, 
                    fileSelection: fileSelection,
                    datasetAccess: datasetAccess);

            var fillValue = InternalFillValue.Value is null
                ? default
                : MemoryMarshal.Cast<byte, TResult>(InternalFillValue.Value)[0];

            var result = await ReadCoreAsync(
                reader,
                destination,
                converter,
                readVirtualDelegate,
                fillValue,
                factor,
                fileSelection,
                memorySelection,
                memoryDims,
                datasetAccess,
                skipShuffle: skipShuffle
            );

            /* ensure correct endianness */
            if (InternalDataType.BitField is IByteOrderAware byteOrderAware)
            {
                Utils.EnsureEndianness(
                    source: MemoryMarshal.AsBytes(result.AsSpan()).ToArray() /* make copy of array */, 
                    destination: MemoryMarshal.AsBytes(result.AsSpan()), 
                    byteOrderAware.ByteOrder, 
                    InternalDataType.Size);
            }

            return result;
        }

        internal async Task<TResult[]?> ReadCoreReferenceAsync<TResult, TReader>(
            TReader reader,
            Memory<TResult> destination,
            Action<Memory<byte>, Memory<TResult>> converter,
            Selection? fileSelection = default,
            Selection? memorySelection = default,
            ulong[]? memoryDims = default,
            H5DatasetAccess datasetAccess = default,
            bool skipShuffle = false)
                where TReader : IReader
        {
            Task readVirtualDelegate(H5Dataset dataset, Memory<TResult> destination, Selection fileSelection, H5DatasetAccess datasetAccess)
                => dataset.ReadCoreReferenceAsync(
                    reader,
                    destination,
                    converter,
                    fileSelection: fileSelection,
                    datasetAccess: datasetAccess);

            using var fillValueArrayOwner = MemoryPool<TResult>.Shared.Rent(1);
            var fillValueArray = fillValueArrayOwner.Memory[..1];
            var fillValue = default(TResult);

            if (InternalFillValue.Value is not null)
            {
                converter(InternalFillValue.Value, fillValueArray);
                fillValue = fillValueArray.Span[0];
            }

            var result = await ReadCoreAsync(
                reader,
                destination,
                converter,
                readVirtualDelegate,
                fillValue,
                factor: 1,
                fileSelection,
                memorySelection,
                memoryDims,
                datasetAccess,
                skipShuffle: skipShuffle
            );

            return result!;
        }

        internal async Task<TResult[]?> ReadCoreAsync<TResult, TReader>(
            TReader reader,
            Memory<TResult> destination,
            Action<Memory<byte>, Memory<TResult>> converter,
            ReadVirtualDelegate<TResult> readVirtualDelegate,
            TResult? fillValue,
            int factor,
            Selection? fileSelection = default,
            Selection? memorySelection = default,
            ulong[]? memoryDims = default,
            H5DatasetAccess datasetAccess = default,
            bool skipShuffle = false)
                where TReader : IReader
        {
            // fast path for null dataspace
            if (InternalDataspace.Type == DataspaceType.Null)
                return Array.Empty<TResult>();

            // for testing only
            if (skipShuffle && InternalFilterPipeline is not null)
            {
                var filtersToRemove = InternalFilterPipeline
                    .FilterDescriptions
                    .Where(description => description.Identifier == FilterIdentifier.Shuffle)
                    .ToList();

                foreach (var filter in filtersToRemove)
                {
                    InternalFilterPipeline.FilterDescriptions.Remove(filter);
                }
            }

            /* buffer provider */
            using H5D_Base h5d = InternalDataLayout.LayoutClass switch
            {
                /* Compact: The array is stored in one contiguous block as part of
                 * this object header message. 
                 */
                LayoutClass.Compact => new H5D_Compact(this, datasetAccess),

                /* Contiguous: The array is stored in one contiguous area of the file. 
                * This layout requires that the size of the array be constant: 
                * data manipulations such as chunking, compression, checksums, 
                * or encryption are not permitted. The message stores the total
                * storage size of the array. The offset of an element from the 
                * beginning of the storage area is computed as in a C array.
                */
                LayoutClass.Contiguous => new H5D_Contiguous(this, datasetAccess),

                /* Chunked: The array domain is regularly decomposed into chunks,
                 * and each chunk is allocated and stored separately. This layout 
                 * supports arbitrary element traversals, compression, encryption,
                 * and checksums (these features are described in other messages).
                 * The message stores the size of a chunk instead of the size of the
                 * entire array; the storage size of the entire array can be 
                 * calculated by traversing the chunk index that stores the chunk 
                 * addresses. 
                 */
                LayoutClass.Chunked => H5D_Chunk.Create(this, datasetAccess),

                /* Virtual: This is only supported for version 4 of the Data Layout 
                 * message. The message stores information that is used to locate 
                 * the global heap collection containing the Virtual Dataset (VDS) 
                 * mapping information. The mapping associates the VDS to the source
                 * dataset elements that are stored across a collection of HDF5 files.
                 */
                LayoutClass.VirtualStorage => new H5D_Virtual<TResult>(this, datasetAccess, fillValue, readVirtualDelegate),

                /* default */
                _ => throw new Exception($"The data layout class '{InternalDataLayout.LayoutClass}' is not supported.")
            };

            h5d.Initialize();

            /* dataset dims */
            var datasetDims = GetDatasetDims();

            /* dataset chunk dims */
            var datasetChunkDims = h5d.GetChunkDims();

            /* file selection */
            if (fileSelection is null)
            {
                switch (InternalDataspace.Type)
                {
                    case DataspaceType.Scalar:
                    case DataspaceType.Simple:

                        var starts = datasetDims.ToArray();
                        starts.AsSpan().Clear();

                        fileSelection = new HyperslabSelection(rank: datasetDims.Length, starts: starts, blocks: datasetDims);

                        break;

                    case DataspaceType.Null:
                    default:
                        throw new Exception($"Unsupported data space type '{InternalDataspace.Type}'.");
                }
            }

            /* memory dims */
            var sourceElementCount = fileSelection.TotalElementCount;

            if (memorySelection is not null && memoryDims is null)
                throw new Exception("If a memory selection is specified, the memory dimensions must be specified, too.");

            memoryDims ??= new ulong[] { sourceElementCount };

            /* memory selection */
            memorySelection ??= new HyperslabSelection(start: 0, block: sourceElementCount);

            /* target buffer */
            var destinationElementCount = Utils.CalculateSize(memoryDims);
            var destinationElementCountScaled = destinationElementCount * (ulong)factor;

            EnsureBuffer(destination, destinationElementCountScaled, out var optionalDestinationArray);
            var destinationMemory = optionalDestinationArray ?? destination;

            /* copy info */
            var copyInfo = new CopyInfo<TResult>(
                datasetDims,
                datasetChunkDims,
                memoryDims,
                memoryDims,
                fileSelection,
                memorySelection,
                GetSourceStreamAsync: chunkIndices => h5d.GetStreamAsync(reader, chunkIndices),
                GetTargetBuffer: _ => destinationMemory,
                Converter: converter,
                SourceTypeSize: (int)InternalDataType.Size,
                TargetTypeFactor: factor
            );

            await SelectionUtils
                .CopyAsync(reader, datasetChunkDims.Length, memoryDims.Length, copyInfo)
                .ConfigureAwait(false);

            return optionalDestinationArray;
        }

        internal static void EnsureBuffer<TResult>(Memory<TResult> destination, ulong destinationElementCount, out TResult[]? newArray)
        {
            newArray = default;

            // user did not provide buffer
            if (destination.Equals(default))
            {
                // create the buffer
                newArray = new TResult[destinationElementCount];
            }

            // user provided buffer is too small
            else if (destination.Length < (int)destinationElementCount)
            {
                throw new Exception("The provided target buffer is too small.");
            }
        }

        internal ulong[] GetDatasetDims()
        {
            return InternalDataspace.Type switch
            {
                DataspaceType.Scalar => new ulong[] { 1 },
                DataspaceType.Simple => InternalDataspace.DimensionSizes,
                _ => throw new Exception($"Unsupported data space type '{InternalDataspace.Type}'.")
            };
        }

        #endregion
    }
}
