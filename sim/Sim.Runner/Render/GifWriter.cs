using System;
using System.IO;

namespace Urbe.Sim.Runner.Render
{
    /// <summary>
    /// Codificador GIF89a minimo: paleta global fija, LZW de 8 bits, bucle infinito (NETSCAPE2.0) y retardo por fotograma.
    /// No depende de SkiaSharp: recibe fotogramas ya cuantizados a indices de paleta.
    /// </summary>
    public sealed class GifWriter : IDisposable
    {
        private const int HashSize = 5003;

        private readonly Stream _out;
        private readonly bool _leaveOpen;
        private readonly int _width;
        private readonly int _height;
        private readonly ushort _delayCs;
        private readonly byte[] _block = new byte[255];
        private readonly int[] _hashKey = new int[HashSize];
        private readonly int[] _hashVal = new int[HashSize];
        private int _blockLen;
        private uint _bitBuf;
        private int _bitCount;
        private bool _closed;

        public int FrameCount { get; private set; }

        /// <param name="palette">256 colores RGB (768 bytes).</param>
        /// <param name="delayCentiseconds">Retardo por fotograma en centesimas de segundo.</param>
        public GifWriter(Stream output, int width, int height, byte[] palette, int delayCentiseconds, bool leaveOpen = false)
        {
            if (width <= 0 || height <= 0 || width > 65535 || height > 65535) throw new ArgumentOutOfRangeException(nameof(width));
            if (palette == null || palette.Length != 768) throw new ArgumentException("La paleta debe tener 768 bytes.", nameof(palette));
            _out = output;
            _leaveOpen = leaveOpen;
            _width = width;
            _height = height;
            _delayCs = (ushort)Math.Clamp(delayCentiseconds, 0, 65535);

            WriteAscii("GIF89a");
            WriteU16(width);
            WriteU16(height);
            _out.WriteByte(0xF7); // tabla global presente, resolucion de color 8 bits, 256 entradas
            _out.WriteByte(0);    // color de fondo
            _out.WriteByte(0);    // relacion de aspecto
            _out.Write(palette, 0, 768);
            // Extension de aplicacion NETSCAPE2.0: bucle infinito.
            _out.WriteByte(0x21);
            _out.WriteByte(0xFF);
            _out.WriteByte(11);
            WriteAscii("NETSCAPE2.0");
            _out.WriteByte(3);
            _out.WriteByte(1);
            WriteU16(0);
            _out.WriteByte(0);
        }

        /// <summary>Anade un fotograma de <c>width*height</c> indices de paleta (filas de arriba abajo).</summary>
        public void AddFrame(ReadOnlySpan<byte> indices)
        {
            if (_closed) throw new ObjectDisposedException(nameof(GifWriter));
            if (indices.Length != _width * _height) throw new ArgumentException("Tamano de fotograma incorrecto.", nameof(indices));
            // Control grafico: disposal 1 (no tocar), sin transparencia.
            _out.WriteByte(0x21);
            _out.WriteByte(0xF9);
            _out.WriteByte(4);
            _out.WriteByte(0x04);
            WriteU16(_delayCs);
            _out.WriteByte(0);
            _out.WriteByte(0);
            // Descriptor de imagen.
            _out.WriteByte(0x2C);
            WriteU16(0);
            WriteU16(0);
            WriteU16(_width);
            WriteU16(_height);
            _out.WriteByte(0);
            _out.WriteByte(8); // tamano minimo de codigo LZW
            EncodeLzw(indices);
            _out.WriteByte(0); // fin de datos de imagen
            FrameCount++;
        }

        private void EncodeLzw(ReadOnlySpan<byte> data)
        {
            const int clear = 256, eoi = 257;
            int nextCode = 258;
            int codeSize = 9;
            _blockLen = 0;
            _bitBuf = 0;
            _bitCount = 0;
            Array.Fill(_hashKey, -1);

            WriteCode(clear, codeSize);
            int prefix = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                int c = data[i];
                int key = (prefix << 8) | c;
                int h = (int)(((uint)key * 2654435761u) % HashSize);
                int found = -1;
                while (_hashKey[h] != -1)
                {
                    if (_hashKey[h] == key) { found = _hashVal[h]; break; }
                    h++;
                    if (h == HashSize) h = 0;
                }
                if (found >= 0) { prefix = found; continue; }

                WriteCode(prefix, codeSize);
                if (nextCode < 4096)
                {
                    _hashKey[h] = key;
                    _hashVal[h] = nextCode;
                    if (nextCode == (1 << codeSize) && codeSize < 12) codeSize++;
                    nextCode++;
                }
                else
                {
                    WriteCode(clear, codeSize);
                    Array.Fill(_hashKey, -1);
                    nextCode = 258;
                    codeSize = 9;
                }
                prefix = c;
            }
            WriteCode(prefix, codeSize);
            // El decodificador anade una entrada tras cada codigo; el ancho para EOI debe reflejarlo.
            if (nextCode < 4096 && nextCode == (1 << codeSize) && codeSize < 12) codeSize++;
            WriteCode(eoi, codeSize);
            if (_bitCount > 0) PutByte((byte)(_bitBuf & 0xFF));
            FlushBlock();
        }

        private void WriteCode(int code, int size)
        {
            _bitBuf |= (uint)code << _bitCount;
            _bitCount += size;
            while (_bitCount >= 8)
            {
                PutByte((byte)(_bitBuf & 0xFF));
                _bitBuf >>= 8;
                _bitCount -= 8;
            }
        }

        private void PutByte(byte b)
        {
            _block[_blockLen++] = b;
            if (_blockLen == 255) FlushBlock();
        }

        private void FlushBlock()
        {
            if (_blockLen == 0) return;
            _out.WriteByte((byte)_blockLen);
            _out.Write(_block, 0, _blockLen);
            _blockLen = 0;
        }

        /// <summary>Escribe el trailer (0x3B). Idempotente.</summary>
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _out.WriteByte(0x3B);
            _out.Flush();
            if (!_leaveOpen) _out.Dispose();
        }

        private void WriteU16(int v)
        {
            _out.WriteByte((byte)(v & 0xFF));
            _out.WriteByte((byte)((v >> 8) & 0xFF));
        }

        private void WriteAscii(string s)
        {
            for (int i = 0; i < s.Length; i++) _out.WriteByte((byte)s[i]);
        }
    }
}
