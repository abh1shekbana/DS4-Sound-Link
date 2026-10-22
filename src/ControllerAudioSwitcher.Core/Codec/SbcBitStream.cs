namespace ControllerAudioSwitcher.Core.Codec;

/// <summary>
/// Bit-level stream writer for SBC packet serialization.
/// </summary>
public sealed class SbcBitStream
{
    private byte[] _buffer;
    private int _length;
    private int _byteOffset;
    private int _bitOffset;
    public bool HasError { get; private set; }

    public SbcBitStream(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
        Reset(buffer, length);
    }

    public void Reset(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
        _byteOffset = 0;
        _bitOffset = 0;
        HasError = false;
    }

    public void PutBits(uint value, int count)
    {
        if (count <= 0 || HasError) return;

        for (int i = count - 1; i >= 0; i--)
        {
            if (_byteOffset >= _length)
            {
                HasError = true;
                return;
            }

            uint bit = (value >> i) & 1u;
            if (bit != 0)
            {
                _buffer[_byteOffset] |= (byte)(1 << (7 - _bitOffset));
            }
            else
            {
                _buffer[_byteOffset] &= (byte)~(1 << (7 - _bitOffset));
            }

            _bitOffset++;
            if (_bitOffset == 8)
            {
                _bitOffset = 0;
                _byteOffset++;
            }
        }
    }

    public void Flush()
    {
        if (_bitOffset > 0 && _byteOffset < _length)
        {
            _byteOffset++;
            _bitOffset = 0;
        }
    }
}
