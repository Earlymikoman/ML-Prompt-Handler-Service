using System.Net;

using Azure.Identity;
using Microsoft.Azure.Cosmos;

using AzureFileServer.Utils;
using AzureFileServer.FileServer;

namespace AzureFileServer.Azure;

// A very simple wrapper to make it easier to call CosmosDb APIs
public class CosmosDbWrapper
{
    private readonly Logger _logger;

    private readonly CosmosClient _client;
    private readonly Container _container;

    public CosmosDbWrapper(IConfiguration configuration)
    {
        if (null == configuration)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        string serviceName = configuration["Logging:ServiceName"];
        _logger = new Logger(serviceName);
        
        string cosmosEndpoint = configuration["AzureFileServer:ConnectionStrings:CosmosEndpoint"];
        string cosmosConnectionString = configuration["AzureFileServer:ConnectionStrings:CosmosConnectionString"];
        if (string.IsNullOrEmpty(cosmosConnectionString))
        {
            _client = new CosmosClient(cosmosEndpoint, new DefaultAzureCredential());
        }
        else
        {
            _client = new CosmosClient(cosmosConnectionString);
        }

        string databaseName = configuration["AzureFileServer:ConnectionStrings:CosmosDatabaseName"];
        string containerName = configuration["AzureFileServer:ConnectionStrings:CosmosContainerName"];
        _container = _client.GetContainer(databaseName, containerName);
    }

    public async Task AddItemAsync<T>(T item, string pk)
    {
        if (null == item)
        {
            throw new ArgumentException("Item cannot be null", nameof(item));
        }
        if (string.IsNullOrEmpty(pk))
        {
            throw new ArgumentException("Partition key cannot be null or empty", nameof(pk));
        }

        using (var log = _logger.StartMethod(nameof(AddItemAsync)))
        {
            log.SetAttribute("item", item.ToString());
            log.SetAttribute("pk", pk);
            await _container.CreateItemAsync(item, new PartitionKey(pk));
        }
    }

    public async Task<T> GetItemAsync<T>(string id, string pk)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("Id cannot be null or empty", nameof(id));
        }
        if (string.IsNullOrEmpty(pk))
        {
            throw new ArgumentException("Partition key cannot be null or empty", nameof(pk));
        }

        try
        {
            using (var log = _logger.StartMethod(nameof(GetItemAsync)))
            {
                log.SetAttribute("id", id);
                log.SetAttribute("pk", pk);
                ItemResponse<T> response = await _container.ReadItemAsync<T>(id, new PartitionKey(pk));
                return response.Resource;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }
    }

    public async Task<IEnumerable<T>> GetItemsAsync<T>(string queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            throw new ArgumentException("Query string cannot be null or empty", nameof(queryString));
        }

        using (var log = _logger.StartMethod(nameof(GetItemAsync)))
        {
            log.SetAttribute("query", queryString);

            var query = _container.GetItemQueryIterator<T>(new QueryDefinition(queryString));
            List<T> results = new List<T>();
            while (query.HasMoreResults)
            {
                FeedResponse<T> response = await query.ReadNextAsync();
                results.AddRange(response);
            }

            return results;
        }
    }

    public async Task UpdateItemAsync<T>(string id, string pk, T item)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("Id cannot be null or empty", nameof(id));
        }
        if (string.IsNullOrEmpty(pk))
        {
            throw new ArgumentException("Partition key cannot be null or empty", nameof(pk));
        }
        if (null == item)
        {
            throw new ArgumentException("Item cannot be null", nameof(item));
        }
        
        using (var log = _logger.StartMethod(nameof(UpdateItemAsync)))
        {
            log.SetAttribute("id", id);
            log.SetAttribute("pk", pk);
            log.SetAttribute("item", item.ToString());
            await _container.UpsertItemAsync(item, new PartitionKey(pk));
        }
    }

    public async Task DeleteItemAsync(string id, string pk)
    {
        using (var log = _logger.StartMethod(nameof(DeleteItemAsync)))
        {
            log.SetAttribute("id", id);
            await _container.DeleteItemAsync<FileMetadata>(id, new PartitionKey(pk));
        }
    }
}