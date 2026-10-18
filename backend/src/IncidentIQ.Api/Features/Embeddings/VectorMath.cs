namespace IncidentIQ.Api.Features.Embeddings;

public static class VectorMath
{
    public static double Norm(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
        {
            throw new InvalidDataException("An embedding vector must not be empty.");
        }

        double sum = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("An embedding vector must contain only finite values.");
            }

            sum += (double)value * value;
        }

        if (sum == 0)
        {
            throw new InvalidDataException("An embedding vector must not be a zero vector.");
        }

        return Math.Sqrt(sum);
    }

    public static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new InvalidDataException("Embedding vector dimensions must match.");
        }

        var denominator = Norm(left) * Norm(right);
        double dot = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += (double)left[index] * right[index];
        }

        return Math.Clamp(dot / denominator, -1, 1);
    }
}
