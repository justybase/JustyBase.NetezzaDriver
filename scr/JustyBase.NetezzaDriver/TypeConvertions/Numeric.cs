using System.Globalization;

namespace JustyBase.NetezzaDriver.TypeConvertions;

/// <summary>
/// Provides methods for handling numeric data.
/// </summary>
internal sealed class Numeric
{
    //public const bool NDIGIT_INT64 = false;
    private const int MAX_NUMERIC_DIGIT_COUNT = 4;
    private const int NUMERIC_MAX_PRECISION = 38;
    private const ulong HI32_MASK = 0xffffffff00000000;

    // Sign mask for the flags field. A value of zero in this bit indicates a
    // positive Decimal value, and a value of one in this bit indicates a
    // negative Decimal value.
    private const int ScaleMask = 0x00FF0000;
    // Scale mask for the flags field. This byte in the flags field contains
    // the power of 10 to divide the Decimal value by. The scale byte must
    // contain a value between 0 and 28 inclusive.

    private const int SignMask = unchecked((int)0x80000000);
    private const uint SignMaskUint = 0x80000000;

    /// <summary>
    /// Checks if the given flags represent a valid decimal.
    /// </summary>
    /// <param name="f">The flags to check.</param>
    /// <returns>True if the flags represent a valid decimal; otherwise, false.</returns>
    private static bool IsValidDecimal(int f)
    {
        return (f & ~(SignMask | ScaleMask)) == 0 && (f & ScaleMask) <= 28 << 16;
    }


    /// <summary>
    /// Divides a 128-bit number by 10.
    /// </summary>
    /// <param name="numerator">The 128-bit number to divide.</param>
    /// <returns>The remainder of the division.</returns>
    private static int Div10_128(Span<long> numerator)
    {
        int remainder = 0;
        for (int index = 0; index < MAX_NUMERIC_DIGIT_COUNT; ++index)
        {
            long work = numerator[index] + ((long)remainder << 32);
            if (work != 0L)
            {
                numerator[index] = work / 10L;
                remainder = (int)(work % 10L);
            }
            else
            {
                numerator[index] = 0L;
                remainder = 0;
            }
        }
        return remainder;
    }

    /// <summary>
    /// Checks if the numeric data is negative.
    /// </summary>
    /// <param name="numdataP">The numeric data to check.</param>
    /// <returns>True if the numeric data is negative; otherwise, false.</returns>
    private static bool IsNumericDataNegative(Span<long> numdataP)
    {
        return (numdataP[0] & SignMaskUint) != 0;
    }


    /// <summary>
    /// Negates a 128-bit number.
    /// </summary>
    /// <param name="data">The 128-bit number to negate.</param>
    /// <returns>True if the negation resulted in a negative number; otherwise, false.</returns>
    private static bool Negate128(Span<long> data)
    {
        // First complement the value (1's complement)
        for (int i = 0; i < MAX_NUMERIC_DIGIT_COUNT; ++i)
        {
            data[i] = ~(int)data[i] & uint.MaxValue;
        }

        // Then increment it to form 2's complement (negative)
        return Inc128(data);
    }

    private const long HI32_MASK_LONG = unchecked((long)HI32_MASK);

    /// <summary>
    /// #for 2's complement Increments a 128-bit number.
    /// </summary>
    /// <param name="arg">The 128-bit number to increment.</param>
    /// <returns>True if the increment resulted in a negative number; otherwise, false.</returns>
    private static bool Inc128(Span<long> arg)
    {
        int i = MAX_NUMERIC_DIGIT_COUNT;
        bool carry = true;
        bool bInputNegative = IsNumericDataNegative(arg);

        while (i != 0 & carry)
        {
            i -= 1;
            long work = arg[i] + 1;
            carry = (work & HI32_MASK_LONG) != 0;
            arg[i] = work & uint.MaxValue;
        }
        return !bInputNegative && IsNumericDataNegative(arg);
    }

    private readonly static CultureInfo _invariantCultureInfo = CultureInfo.InvariantCulture;

    /// <summary>
    /// Converts numeric data to a decimal.
    /// </summary>
    /// <param name="data">The numeric data to convert.</param>
    /// <param name="prec">The precision of the numeric data.</param>
    /// <param name="scale">The scale of the numeric data.</param>
    /// <param name="digitCount">The number of digits in the numeric data.</param>
    /// <returns>The converted decimal value.</returns>
    public static decimal GetCsNumeric(ReadOnlySpan<byte> data, int prec, int scale, int digitCount)
    {
        int numParts = prec <= 9 ? 1 : prec <= 18 ? 2 : 4;

        Span<long> dataP = stackalloc long[4];
        for (int index = 0; index < numParts; ++index)
        {
            dataP[index] = BitConverter.ToUInt32(data[(index * 4)..]);
        }

        Span<long> varPdata = stackalloc long[4];
        var sign = (dataP[0] & SignMaskUint) != 0 ? -1 : 0;
        int i = 0;
        while (i < MAX_NUMERIC_DIGIT_COUNT - digitCount)
        {
            varPdata[i] = sign;
            i++;
        }
        int j = 0;

        while (i < MAX_NUMERIC_DIGIT_COUNT)
        {
            varPdata[i] = (uint)dataP[j];
            i++;
            j++;
        }
        //varPdata.CopyTo(dataP);

        bool isMinus = IsNumericDataNegative(varPdata);
        //if negative -> negate, Negate128 mutates varPdata
        if (isMinus && Negate128(varPdata))
            throw new FormatException("Numeric value could not be converted from Netezza binary format.");

        if (IsValidDecimal((int)varPdata[0]))
        {
            var dc1 = new decimal((int)varPdata[3], (int)varPdata[2], (int)varPdata[1], isMinus, (byte)scale);
            return dc1;
        }

        return GetNumericFromChars(scale, varPdata, isMinus);
    }

    /// <summary>
    /// Converts numeric data to a decimal using character representation.
    /// </summary>
    /// <param name="scale">The scale of the numeric data.</param>
    /// <param name="dataBufferV2">The numeric data buffer.</param>
    /// <param name="isMinus">Indicates if the numeric data is negative.</param>
    /// <returns>The converted decimal value.</returns>
    private static decimal GetNumericFromChars(int scale, Span<long> dataBufferV2, bool isMinus)
    {
        Span<int> intDigits = stackalloc int[NUMERIC_MAX_PRECISION];
        Span<byte> charDigits = stackalloc byte[NUMERIC_MAX_PRECISION];
        int nm = 0;
        for (int i = 0; i < NUMERIC_MAX_PRECISION; ++i)
            intDigits[NUMERIC_MAX_PRECISION - i - 1] = Div10_128(dataBufferV2);

        bool flag2 = true;
        for (int j = 0; j < NUMERIC_MAX_PRECISION; ++j)
        {
            if (!(j < NUMERIC_MAX_PRECISION - scale - 1 & flag2) || intDigits[j] != 0)
            {
                flag2 = false;
                charDigits[nm++] = (byte)(intDigits[j] + 48);
            }
        }
        int length = nm;
        nm = 0;

        Span<byte> charDigitsWithSignDot = stackalloc byte[length + 2];
        if (isMinus)
        {
            charDigitsWithSignDot[nm++] = (byte)'-';
        }

        if (scale != 0)
        {
            int numbersBeforeDecimalSeparator = length - scale;
            charDigits.Slice(0, numbersBeforeDecimalSeparator).CopyTo(charDigitsWithSignDot.Slice(nm));
            nm += numbersBeforeDecimalSeparator;
            charDigitsWithSignDot[nm++] = (byte)'.';
            charDigits.Slice(numbersBeforeDecimalSeparator, scale).CopyTo(charDigitsWithSignDot.Slice(nm));
            nm += scale;
        }
        else
        {
            charDigits.Slice(0, length).CopyTo(charDigitsWithSignDot.Slice(nm));
            nm += length;
        }
        return decimal.Parse(charDigitsWithSignDot.Slice(0, nm), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, provider: CultureInfo.InvariantCulture);
    }
}
