﻿namespace HDF5.NET
{
    public struct H5Reference
    {
        internal H5Reference(ulong value)
        {
            this.Value = value;
        }

        #region Properties

        internal ulong Value { get; set; }

        #endregion
    }
}
