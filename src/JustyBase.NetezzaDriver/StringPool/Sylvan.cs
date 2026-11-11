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
    /// <summary>
    /// Maximum interned entries per pool. Beyond this, new strings are
    /// returned without interning to bound memory on high-cardinality columns.
    /// Pools are per-query/per-column; 4096 short strings bound the pool to
    /// roughly hundreds of KB worst case instead of growing without limit.
    /// </summary>
    const int MaxEntries = 4096;

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

    // Single-entry last-hit cache. Row data is usually grouped/sorted
    // (month names repeat ~30 rows in a row, flags repeat in runs), so the
    // next lookup very often repeats the previous value. One length check
    // + one SequenceEqual then skips the hash computation and bucket walk
    // entirely; on a miss the cost is just the failed length check.
    // Only covers the pooled path (short strings); the long-string bypass
    // below stays allocation-per-call by design (see unit test).
    string? _lastHit;

    // Encoded bytes of _lastHitBytesSource. Checking a repeated value at the
    // byte level avoids transcoding it to UTF-16 and hashing it *before* the
    // char-level last-hit cache can answer. Rows are grouped, so this is the
    // common case for date dimensions and low-cardinality flags.
    const int MaxTrackedBytes = 4 * StringSizeLimit; // any <=32-char UTF-8 value
    byte[]? _lastHitBytes;
    int _lastHitBytesLength;
    string? _lastHitBytesSource;
    Encoding? _lastHitBytesEncoding;


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

        var last = _lastHit;
        if (last is not null && last.Length == length && buffer.SequenceEqual(last.AsSpan()))
        {
            return last;
        }

        // High-cardinality column: stop interning once the pool is full. Check
        // this before hashing/scanning: after the cap those lookups always miss
        // and just add per-value cost (common for SKU/note-like columns).
        if (count >= MaxEntries)
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
                _lastHit = str;
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

        int currentCount = this.count;
        if (currentCount == entries.Length)
        {
            entries = Resize();
            bucket = ref GetBucket(hashCode);
        }
        int index = currentCount;
        this.count = currentCount + 1;

        str = new string(buffer);

        ref Entry entry = ref entries![index];
        entry.hashCode = hashCode;
        entry.next = bucket - 1;
        entry.str = str;

        bucket = index + 1; // bucket is an int ref
        _lastHit = str;

        return str;
    }

    //[SkipLocalsInit]
    public string GetString(ReadOnlySpan<byte> buffer, Encoding encoding)
    {
        // Byte-level last-hit: a repeated value needs no transcode and no hash.
        var byteCached = _lastHitBytesSource;
        if (byteCached is not null
            && ReferenceEquals(encoding, _lastHitBytesEncoding)
            && buffer.Length == _lastHitBytesLength
            && buffer.SequenceEqual(_lastHitBytes.AsSpan(0, _lastHitBytesLength)))
        {
            return byteCached;
        }

        int maxLength = encoding.GetMaxCharCount(buffer.Length);
        if (maxLength > StringSizeLimit)
        {
            return encoding.GetString(buffer);
        }

        Span<char> chars = stackalloc char[maxLength];
        int effectiveLength = encoding.GetChars(buffer, chars);
        string result = GetString(chars.Slice(0, effectiveLength));
        if (ReferenceEquals(result, _lastHit) && buffer.Length <= MaxTrackedBytes)
        {
            var bytes = _lastHitBytes ??= new byte[MaxTrackedBytes];
            buffer.CopyTo(bytes);
            _lastHitBytesLength = buffer.Length;
            _lastHitBytesSource = result;
            _lastHitBytesEncoding = encoding;
        }
        return result;
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
        return ref buckets[hashCode & ((uint)buckets.Length - 1)];
    }

    struct Entry
    {
        public uint hashCode;
        public int next;
        public string str;
    }
}