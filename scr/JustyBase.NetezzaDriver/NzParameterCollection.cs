using System.Collections;
using System.Data;
using System.Data.Common;

namespace JustyBase.NetezzaDriver;

public sealed class NzParameterCollection : DbParameterCollection
{
    private readonly List<NzParameter> _parameters = [];
    private readonly Dictionary<string, int> _nameIndex = new(StringComparer.OrdinalIgnoreCase);

    public override int Count => _parameters.Count;

    public override object SyncRoot => this;

    public new NzParameter this[int index]
    {
        get => _parameters[index];
        set
        {
            RemoveAt(index);
            Insert(index, value);
        }
    }

    public new NzParameter this[string name]
    {
        get => this[IndexOf(name)];
        set
        {
            var idx = IndexOf(name);
            if (idx >= 0)
                RemoveAt(idx);
            Add(value);
        }
    }

    public int Add(NzParameter parameter)
    {
        var index = _parameters.Count;
        _parameters.Add(parameter);
        if (parameter.ParameterName is { Length: > 0 } && !parameter.IsPositional)
        {
            var resolved = parameter.ResolvedName ?? parameter.ParameterName;
            _nameIndex[resolved] = index;
        }
        return index;
    }

    public NzParameter AddWithValue(string? parameterName, object? value)
    {
        var param = new NzParameter(parameterName, value);
        Add(param);
        return param;
    }

    public override int Add(object value)
    {
        if (value is NzParameter p)
            return Add(p);
        throw new ArgumentException("Value must be of type NzParameter", nameof(value));
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values)
        {
            if (v is NzParameter p)
                Add(p);
        }
    }

    public override void Clear()
    {
        _parameters.Clear();
        _nameIndex.Clear();
    }

    public bool Contains(NzParameter value) => _parameters.Contains(value);

    public override bool Contains(object value) => value is NzParameter p && Contains(p);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index)
    {
        ((ICollection)_parameters).CopyTo(array, index);
    }

    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) => value is NzParameter p ? _parameters.IndexOf(p) : -1;

    public override int IndexOf(string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName))
            return -1;

        var lookup = parameterName;
        if (lookup.Length > 0 && (lookup[0] == ':' || lookup[0] == '@'))
            lookup = lookup[1..];

        if (_nameIndex.TryGetValue(lookup, out var idx))
            return idx;

        for (int i = 0; i < _parameters.Count; i++)
        {
            if (string.Equals(_parameters[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    public override void Insert(int index, object value)
    {
        if (value is NzParameter p)
        {
            _parameters.Insert(index, p);
            if (p.ParameterName is { Length: > 0 } && !p.IsPositional)
                _nameIndex[p.ResolvedName ?? p.ParameterName] = index;
        }
    }

    public override void Remove(object value)
    {
        if (value is NzParameter p)
        {
            var idx = _parameters.IndexOf(p);
            if (idx >= 0)
                RemoveAt(idx);
        }
    }

    public override void RemoveAt(int index)
    {
        var p = _parameters[index];
        _parameters.RemoveAt(index);
        if (p.ParameterName is { Length: > 0 } && !p.IsPositional)
        {
            var resolved = p.ResolvedName ?? p.ParameterName;
            _nameIndex.Remove(resolved);
        }
        RebuildNameIndex();
    }

    public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _parameters[index];

    protected override DbParameter GetParameter(string parameterName) => this[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value)
    {
        if (value is NzParameter p)
            this[index] = p;
    }

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        if (value is NzParameter p)
            this[parameterName] = p;
    }

    private void RebuildNameIndex()
    {
        _nameIndex.Clear();
        for (int i = 0; i < _parameters.Count; i++)
        {
            var p = _parameters[i];
            if (p.ParameterName is { Length: > 0 } && !p.IsPositional)
            {
                var resolved = p.ResolvedName ?? p.ParameterName;
                if (!_nameIndex.ContainsKey(resolved))
                    _nameIndex[resolved] = i;
            }
        }
    }
}
