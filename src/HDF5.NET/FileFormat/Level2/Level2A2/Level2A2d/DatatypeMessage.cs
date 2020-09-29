﻿using System;
using System.Collections.Generic;

namespace HDF5.NET
{
    public class DatatypeMessage : Message
    {
        #region Constructors

        public DatatypeMessage(H5BinaryReader reader) : base(reader)
        {
            this.ClassVersion = reader.ReadByte();

            this.BitField = this.Class switch
            {
                DatatypeMessageClass.FixedPoint     => new FixedPointBitFieldDescription(reader),
                DatatypeMessageClass.FloatingPoint  => new FloatingPointBitFieldDescription(reader),
                DatatypeMessageClass.Time           => new TimeBitFieldDescription(reader),
                DatatypeMessageClass.String         => new StringBitFieldDescription(reader),
                DatatypeMessageClass.BitField       => new BitFieldBitFieldDescription(reader),
                DatatypeMessageClass.Opaque         => new OpaqueBitFieldDescription(reader),
                DatatypeMessageClass.Compount       => new CompoundBitFieldDescription(reader),
                DatatypeMessageClass.Reference      => new ReferenceBitFieldDescription(reader),
                DatatypeMessageClass.Enumerated     => new EnumerationBitFieldDescription(reader),
                DatatypeMessageClass.VariableLength => new VariableLengthBitFieldDescription(reader),
                DatatypeMessageClass.Array          => null,
                _ => throw new NotSupportedException($"The data type message class '{this.Class}' is not supported.")
            };

            this.Size = reader.ReadUInt32();

            var memberCount = 1;

            if (this.Class == DatatypeMessageClass.Compount && this.BitField != null)
                memberCount = ((CompoundBitFieldDescription)this.BitField).MemberCount;

            this.Properties = new List<DatatypePropertyDescription>(memberCount);

            for (int i = 0; i < memberCount; i++)
            {
                DatatypePropertyDescription? properties = (this.Version, this.Class) switch
                {
                    (_, DatatypeMessageClass.FixedPoint) => new FixedPointPropertyDescription(reader),
                    (_, DatatypeMessageClass.FloatingPoint) => new FloatingPointPropertyDescription(reader),
                    (_, DatatypeMessageClass.Time) => new TimePropertyDescription(reader),
                    (_, DatatypeMessageClass.String) => null,
                    (_, DatatypeMessageClass.BitField) => new BitFieldPropertyDescription(reader),
                    (_, DatatypeMessageClass.Opaque) => new OpaquePropertyDescription(reader, this.GetOpaqueTagByteLength()),
                    (1, DatatypeMessageClass.Compount) => new CompoundPropertyDescription1(reader),
                    (2, DatatypeMessageClass.Compount) => new CompoundPropertyDescription2(reader),
                    (3, DatatypeMessageClass.Compount) => new CompoundPropertyDescription3(reader, this.Size),
                    (_, DatatypeMessageClass.Reference) => null,
                    (1, DatatypeMessageClass.Enumerated) => new EnumerationPropertyDescription12(reader, this.Size, this.GetEnumMemberCount()),
                    (2, DatatypeMessageClass.Enumerated) => new EnumerationPropertyDescription12(reader, this.Size, this.GetEnumMemberCount()),
                    (3, DatatypeMessageClass.Enumerated) => new EnumerationPropertyDescription3(reader, this.Size, this.GetEnumMemberCount()),
                    (_, DatatypeMessageClass.VariableLength) => new VariableLengthPropertyDescription(reader),
                    (2, DatatypeMessageClass.Array) => new ArrayPropertyDescription2(reader),
                    (3, DatatypeMessageClass.Array) => new ArrayPropertyDescription3(reader),
                    (_, _) => throw new NotSupportedException($"The class '{this.Class}' is not supported on data type messages of version {this.Version}.")
                };

                if (properties != null)
                    this.Properties.Add(properties);
            }
        }

        #endregion

        #region Properties

        public DatatypeBitFieldDescription? BitField { get; set; }
        public uint Size { get; set; }
        public List<DatatypePropertyDescription> Properties { get; set; }

        public byte Version
        {
            get
            {
                return (byte)(this.ClassVersion >> 4);
            }
            set
            {
                if (!(1 <= value && value <= 15))
                    throw new Exception("The version number must be in the range of 1..3.");

                this.ClassVersion &= 0x0F;                  // clear bits 4-7
                this.ClassVersion |= (byte)(value << 4);    // set bits 4-7, depending on value
            }
        }

        public DatatypeMessageClass Class
        {
            get
            {
                return (DatatypeMessageClass)(this.ClassVersion & 0x0F);
            }
            set
            {
                if (!(0 <= (byte)value && (byte)value <= 10))
                    throw new Exception("The version number must be in the range of 1..3.");

                this.ClassVersion &= 0xF0;          // clear bits 0-3
                this.ClassVersion |= (byte)value;   // set bits 0-3, depending on value
            }
        }

        private byte ClassVersion { get; set; }

        #endregion

        #region Method

        private byte GetOpaqueTagByteLength()
        {
            var opaqueDescription = this.BitField as OpaqueBitFieldDescription;

            if (opaqueDescription != null)
                return opaqueDescription.AsciiTagByteLength;
            else
                throw new FormatException($"For opaque types, the bit field description must be an instance of type '{nameof(OpaqueBitFieldDescription)}'.");
        }

        private ushort GetEnumMemberCount()
        {
            var enumerationDescription = this.BitField as EnumerationBitFieldDescription;

            if (enumerationDescription != null)
                return enumerationDescription.MemberCount;
            else
                throw new FormatException($"For enumeration types, the bit field description must be an instance of type '{nameof(EnumerationBitFieldDescription)}'.");
        }

        #endregion
    }
}
