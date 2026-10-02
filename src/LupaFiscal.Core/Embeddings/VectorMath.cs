using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace LupaFiscal.Core.Embeddings;

public static class VectorMath
{
    /// <summary>Scales the vector to unit length in place (a zero vector stays zero) and returns it.</summary>
    public static float[] Normalize(float[] vector)
    {
        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0) TensorPrimitives.Divide(vector, norm, vector);
        return vector;
    }

    /// <summary>Little-endian float32 bytes, as stored in the index.</summary>
    public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0) throw new InvalidDataException("Vector blob length is not a multiple of 4.");
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }
}
