using System.Runtime.CompilerServices;
using System.Text;

namespace JustyBase.NetezzaDriver.StringPool;

/// <summary>
/// Implements a specialized hashtable to provide string de-duping capabilities.
/// </summary>
/// <remarks>
/// This class is useful for serializers where the data being deserialized might
/// contain highly repetetive string values and allows de-duping such strings while
/// avoiding allocations. 
/// </remarks>
public sealed class Sylvan
{
    const int DefaultCapacity = 64;
    const int StringSizeLimit = 32;
    const int CollisionLimit = 8;

    // This is a greatly-simplified HashSet<string> that only allows additions.
    // and accepts char[] instead of string.

    // An extremely simple, and hopefully fast, hash algorithm.
    static uint GetHashCode(ReadOnlySpan<char> buffer, int offset, int length)
    {
        uint hash = 0;
        for (int i = 0; i < length; i++)
        {
            hash = hash * 31 + buffer[offset++];
        }
        return hash;
    }

    int[] buckets; // contains index into entries offset by -1. So that 0 (default) means empty bucket.
    Entry[] entries;

    int count;


    /// <summary>
    /// Creates a new StringPool instance.
    /// </summary>
    public Sylvan()
    {
        int size = GetSize(DefaultCapacity);
        buckets = new int[size];
        entries = new Entry[size];
    }

    static int GetSize(int capacity)
    {
        var size = DefaultCapacity;
        while (size < capacity)
            size = size * 2;
        return size;
    }

    /// <summary>
    /// Gets a string containing the characters in the input buffer.
    /// </summary>
    public string GetString(ReadOnlySpan<char> buffer)
    {
        var length = buffer.Length;
        var str = string.Empty;
        if (length == 0) return str;
        if (length > StringSizeLimit)
        {
            return new string(buffer);
        }

        var entries = this.entries;
        var hashCode = GetHashCode(buffer, 0, length);

        uint collisionCount = 0;
        ref int bucket = ref GetBucket(hashCode);
        int i = bucket - 1;

        while ((uint)i < (uint)entries.Length)
        {
            ref var e = ref entries[i];
            str = e.str;
            if (e.hashCode == hashCode && buffer.SequenceEqual(str.AsSpan()))
            {
                return str;
            }

            i = e.next;

            collisionCount++;
            if (collisionCount > CollisionLimit)
            {
                // protects against malicious inputs
                // too many collisions give up and let the caller create the string.					
                return new string(buffer);
            }
        }

        int count = this.count;
        if (count == entries.Length)
        {
            entries = Resize();
            bucket = ref GetBucket(hashCode);
        }
        int index = count;
        this.count = count + 1;

        str = new string(buffer);

        ref Entry entry = ref entries![index];
        entry.hashCode = hashCode;
        entry.next = bucket - 1;
        entry.str = str;

        bucket = index + 1; // bucket is an int ref

        return str;
    }

    //[SkipLocalsInit]
    public string GetString(ReadOnlySpan<byte> buffer, Encoding encoding)
    {
        int maxLength = encoding.GetMaxCharCount(buffer.Length);
        if (maxLength > StringSizeLimit)
        {
            return encoding.GetString(buffer);
        }

        Span<char> chars = stackalloc char[maxLength];
        int effectiveLength = encoding.GetChars(buffer, chars);
        return GetString(chars.Slice(0, effectiveLength));
    }


    Entry[] Resize()
    {
        var newSize = GetSize(this.count + 1);

        var entries = new Entry[newSize];

        int count = this.count;
        Array.Copy(this.entries, entries, count);

        buckets = new int[newSize];

        for (int i = 0; i < count; i++)
        {
            if (entries[i].next >= -1)
            {
                ref int bucket = ref GetBucket(entries[i].hashCode);
                entries[i].next = bucket - 1;
                bucket = i + 1;
            }
        }

        this.entries = entries;
        return entries;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ref int GetBucket(uint hashCode)
    {
        int[] buckets = this.buckets;
        return ref buckets[hashCode & (uint)buckets.Length - 1];
    }

    struct Entry
    {
        public uint hashCode;
        public int next;
        public string str;
    }
}