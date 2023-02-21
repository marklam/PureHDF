﻿namespace PureHDF
{
    internal class H5D_Virtual<TResult> : H5D_Base
    {
        #region Fields

        private readonly VdsGlobalHeapBlock _block;
        private readonly TResult? _fillValue;
        private readonly ReadVirtualDelegate<TResult> _readVirtualDelegate;

        #endregion

        #region Constructors

        public H5D_Virtual(
            H5Dataset dataset, 
            H5DatasetAccess datasetAccess,
            TResult? fillValue,
            ReadVirtualDelegate<TResult> readVirtualDelegate) 
            : base(dataset, datasetAccess)
        {
            _fillValue = fillValue;
            _readVirtualDelegate = readVirtualDelegate;

            var layoutMessage = (DataLayoutMessage4)dataset.InternalDataLayout;
            var collection = H5Cache.GetGlobalHeapObject(dataset.Context, layoutMessage.Address);
            var index = ((VirtualStoragePropertyDescription)layoutMessage.Properties).Index;
            var objectData = collection.GlobalHeapObjects[(int)index].ObjectData;
            using var localReader = new H5StreamReader(new MemoryStream(objectData), leaveOpen: false);

            _block = new VdsGlobalHeapBlock(localReader, dataset.Context.Superblock);

            // https://docs.hdfgroup.org/archive/support/HDF5/docNewFeatures/VDS/HDF5-VDS-requirements-use-cases-2014-12-10.pdf
            // "A source dataset may have different rank and dimension sizes than the VDS. However, if a
            // source dataset has an unlimited dimension, it must be the slowest-­changing dimension, and
            // the virtual dataset must be the same rank and have the same dimension as unlimited."

            // -> for now unlimited dimensions will not be supported

            foreach (var dimension in Dataset.InternalDataspace.DimensionSizes)
            {
                if (dimension == H5Constants.Unlimited)
                    throw new Exception("Virtual datasets with unlimited dimensions are not supported.");
            }
        }

        #endregion

        #region Properties

        #endregion

        #region Methods

        public override ulong[] GetChunkDims()
        {
            return Dataset.InternalDataspace.DimensionSizes;
        }

        public override Task<Stream> GetStreamAsync<TReader>(TReader reader, ulong[] chunkIndices)
        {
            Stream stream = new VirtualDatasetStream<TResult>(
                Dataset.File,
                _block.VdsDatasetEntries, 
                dimensions: Dataset.InternalDataspace.DimensionSizes,
                fillValue: _fillValue,
                DatasetAccess,
                _readVirtualDelegate
            );

            return Task.FromResult(stream);
        }

        #endregion
    }
}
