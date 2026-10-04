using System.Buffers;
using System.Runtime.CompilerServices;

namespace JustyBase.NetezzaDriver;

/// <summary>
/// Minimal allocation-friendly string builder for synchronous callers. Uses a
/// caller-provided <c>stackalloc</c> buffer for small results and falls back to
/// <see cref="ArrayPool{T}"/> once it grows. The final <see cref="ToString"/>
/// allocates exactly one string and returns any pooled buffer.
/// </summary>
internal ref struct ValueStringBuilder
{
    private char[]? _rented;
    private Span<char> _chars;
    private int _pos;

    public ValueStringBuilder(Span<char> initialBuffer)
    {
        _rented = null;
        _chars = initialBuffer;
        _pos = 0;
    }

    public ValueStringBuilder(char[] rentedBuffer)
    {
        _rented = rentedBuffer;
        _chars = rentedBuffer;
        _pos = 0;
    }

    public readonly int Length => _pos;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(char c)
    {
        int pos = _pos;
        if ((uint)pos < (uint)_chars.Length)
        {
            _chars[pos] = c;
            _pos = pos + 1;
        }
        else
        {
            GrowAndAppend(c);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return;
        Append(s.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(string s, int start, int count)
    {
        if (count <= 0)
            return;
        Append(s.AsSpan(start, count));
    }

    public void Append(scoped ReadOnlySpan<char> s)
    {
        int pos = _pos;
        if (pos > _chars.Length - s.Length)
        {
            Grow(s.Length);
            pos = _pos;
        }
        s.CopyTo(_chars[pos..]);
        _pos = pos + s.Length;
    }

    private void GrowAndAppend(char c)
    {
        Grow(1);
        _chars[_pos++] = c;
    }

    private void Grow(int additional)
    {
        int required = _pos + additional;
        int newLength = Math.Max(required, _chars.Length == 0 ? 16 : _chars.Length * 2);
        char[] next = ArrayPool<char>.Shared.Rent(newLength);
        _chars[.._pos].CopyTo(next);
        if (_rented is not null)
            ArrayPool<char>.Shared.Return(_rented);
        _rented = next;
        _chars = next;
    }

    public override string ToString()
    {
        string result = new string(_chars[.._pos]);
        Dispose();
        return result;
    }

    public void Dispose()
    {
        char[]? rented = _rented;
        this = default;
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
    }
}
