using System.Text;
using System.Text.RegularExpressions;
using chatbot_console.Services;
using Microsoft.Extensions.AI;
using OllamaSharp;

const string embedModel = "nomic-embed-text";
const string chatModel = "llama3:8b";
const string ollamaUrl = "http://localhost:11434";

IOllamaApiClient embedClient = new OllamaApiClient(new Uri(ollamaUrl), embedModel);
IChatClient chatClient = new OllamaApiClient(new Uri(ollamaUrl), chatModel);

async Task SetupRagSystemAsync()
{
    Console.WriteLine("Setting up RAG System...");
    string path = @"D:\case-study\backend\chatbot_console\chatbot_console\Docs";

    foreach (string file in Directory.GetFiles(path, "*.txt"))
    {
        try
        {
            var fileName = Path.GetFileName(file);

            if (string.IsNullOrEmpty(fileName))
            {
                Console.WriteLine("No files were found in RAG System.");
                continue;
            }

            Console.WriteLine($"{fileName} is founded.");

            string content = File.ReadAllText(file);

            // Use a more robust chunking strategy (e.g., split by paragraphs).
            string[] chunks = Regex.Split(content, @"\n\s*\n");

            foreach (string chunk in chunks)
            {
                if (string.IsNullOrEmpty(chunk)) continue;

                var embeddingResponse = await embedClient.EmbedAsync(chunk);
                InMemoryVector.AddChunk(new()
                {
                    Text = chunk,
                    Embeddings = embeddingResponse.Embeddings[0]
                });
            }

            Console.WriteLine("RAG System. Done!");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            throw;
        }
    }
}

async Task ProcessQueryAsync(string prompt)
{
    Console.WriteLine("Processing query...");
    try
    {
        var embeddingResponse = await embedClient.EmbedAsync(prompt);

        var relevantChunks = await InMemoryVector.FindRelevantChunksAsync(embeddingResponse.Embeddings[0]);

        var contextBuilder = new StringBuilder();

        foreach (var chunk in relevantChunks)
        {
            contextBuilder.AppendLine(chunk.Text);
            contextBuilder.AppendLine("---");
        }

//         string systemPrompt = $"""
//                                You are an assistant for question-answering tasks. 
//                                Use the following context to answer the question. 
//                                If you don't know the answer, just say that You don't have any document reference.
//                                Keep the answer concise and based on the provided context only: {contextBuilder};
//                                """;

        string systemPrompt = contextBuilder.ToString();

        List<ChatMessage> messages = [];

        messages.Add(new(ChatRole.System, systemPrompt));
        messages.Add(new(ChatRole.User, prompt));

        StringBuilder responseBuilder = new StringBuilder();

        var chatResponse = chatClient.GetStreamingResponseAsync(messages);

        await foreach (var response in chatResponse)
        {
            responseBuilder.Append(response.Text);
            Console.Write(response.Text);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
        throw;
    }
}

await SetupRagSystemAsync();

Console.WriteLine("==========Chatbot==========");

while (true)
{
    Console.WriteLine("Type 'exit' to close the application");

    var userPrompt = string.Empty;
    userPrompt = Console.ReadLine();
    if (!string.IsNullOrEmpty(userPrompt) && userPrompt.Equals("exit"))
    {
        Console.WriteLine("Exiting from the application\n");
        return;
    }

    if (!string.IsNullOrEmpty(userPrompt))
    {
        await ProcessQueryAsync(userPrompt);
    }
}