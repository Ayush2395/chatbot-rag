namespace chatbot_console.Services;

internal sealed class InMemoryVector
{
    private static readonly List<DocumentChunk> Chunks = [];

    public static void AddChunk(DocumentChunk chunk) => Chunks.Add(chunk);

    public static async Task<IEnumerable<DocumentChunk>> FindRelevantChunksAsync(float[] queryEmbedding,
        int topK = 3, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Finding relevant chunks");
        if (queryEmbedding.Length == 0)
        {
            throw new ArgumentException("queryEmbedding is empty");
        }

        var similarities = new List<(DocumentChunk chunk, double similarity)>();

        foreach (var chunk in Chunks)
        {
            if (chunk.Embeddings.Length == 0)
            {
                continue;
            }

            var similarity = CosineSimilarity(queryEmbedding, chunk.Embeddings);
            similarities.Add((chunk, similarity));
        }

        return await Task.FromResult(
            similarities
                .OrderByDescending(x => x.similarity)
                .Take(topK)
                .Select(rec => rec.chunk));
    }

    private static double CosineSimilarity(float[] vector1, float[] vector2)
    {
        if (vector1.Length != vector2.Length)
        {
            throw new ArgumentException("Vectors must have the same length.");
        }

        var dotProduct = 0.0;
        var magnitude1 = 0.0;
        var magnitude2 = 0.0;

        var i = 0;
        for (; i < vector1.Length; i++)
        {
            dotProduct += (double)vector1[i] * vector2[i];
            magnitude1 += Math.Pow(vector1[i], 2);
            magnitude2 += Math.Pow(vector2[i], 2);
        }

        return dotProduct / (Math.Sqrt(magnitude1) * Math.Sqrt(magnitude2));
    }
}

internal record DocumentChunk
{
    public Guid Id => Guid.CreateVersion7();
    public string? Text { get; set; }
    public float[] Embeddings { get; set; } = [];
}