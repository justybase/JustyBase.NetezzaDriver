using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace JustyBase.NetezzaDriver;

[StructLayout(LayoutKind.Explicit)]
public struct RowValue
{
    [FieldOffset(8)]
    public TypeCodeEx typeCode;
    [FieldOffset(12)]
    public bool boolValue;
    [FieldOffset(12)]
    public char charValue;
    [FieldOffset(12)]
    public sbyte sbyteValue;
    [FieldOffset(12)]
    public byte byteValue;
    [FieldOffset(12)]
    public Int16 int16Value;
    [FieldOffset(12)]
    public UInt16 uint16Value;
    [FieldOffset(12)]
    public UInt32 uint32Value;
    [FieldOffset(12)]
    public Int32 int32Value;
    [FieldOffset(12)]
    public UInt64 uint64Value;
    [FieldOffset(12)]
    public Int64 int64Value;
    [FieldOffset(12)]
    public float singleValue;
    [FieldOffset(12)]
    public double doubleValue;
    [FieldOffset(12)]
    public decimal decimalValue;
    [FieldOffset(12)]
    public DateTime dateTimeValue;
    [FieldOffset(12)]
    public TimeSpan timeSpanValue;
    [FieldOffset(0)]
    public string stringValue;
    [FieldOffset(0)]
    public object objectValue;
    public readonly object GetValue() => typeCode switch
    {
        TypeCodeEx.Empty or TypeCodeEx.DBNull => DBNull.Value,
        TypeCodeEx.Boolean => boolValue,
        TypeCodeEx.Char => charValue,
        TypeCodeEx.SByte => sbyteValue,
        TypeCodeEx.Byte => byteValue,
        TypeCodeEx.Int16 => int16Value,
        TypeCodeEx.UInt16 => uint16Value,
        TypeCodeEx.UInt32 => uint32Value,
        TypeCodeEx.Int32 => int32Value,
        TypeCodeEx.UInt64 => uint64Value,
        TypeCodeEx.Int64 => int64Value,
        TypeCodeEx.Single => singleValue,
        TypeCodeEx.Double => doubleValue,
        TypeCodeEx.Decimal => decimalValue,
        TypeCodeEx.DateTime => dateTimeValue,
        TypeCodeEx.TimeSpan => timeSpanValue,
        TypeCodeEx.String => stringValue,
        TypeCodeEx.Object => objectValue,
        _ => objectValue,
    };

}

public enum TypeCodeEx : int
{
    Empty = 0,
    Object = 1,
    DBNull = 2,
    Boolean = 3,
    Char = 4,
    SByte = 5,
    Byte = 6,
    Int16 = 7,
    UInt16 = 8,
    Int32 = 9,
    UInt32 = 10,
    Int64 = 11,
    UInt64 = 12,
    Single = 13,
    Double = 14,
    Decimal = 15,
    DateTime = 16,
    TimeSpan = 17,
    String = 18,
}