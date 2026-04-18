
using MLPromptHandler.Azure;
using MLPromptHandler.Utils;
using Microsoft.Extensions.Primitives;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace MLPromptHandler.PromptHandler;

// This is the core logic of the web server and hosts all of the HTTP
// handlers used by the web server regarding File Server functionality.
public class PromptHandlerHandlers
{
    //StackOverflow https://stackoverflow.com/questions/12416249/hashing-a-string-with-sha256
    string QuickHash(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var inputHash = SHA256.HashData(inputBytes);
        return Convert.ToHexString(inputHash);
    }

    private readonly IConfiguration _configuration;
    private readonly Logger _logger;
    private readonly CosmosDbWrapper _cosmosDbWrapper;

    public PromptHandlerHandlers(IConfiguration configuration)
    {
        _configuration = configuration;
        if (null == _configuration)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        string serviceName = configuration["Logging:ServiceName"];
        _logger = new Logger(serviceName);

        _cosmosDbWrapper = new CosmosDbWrapper(configuration);
    }

    private static string GetParameterFromList(string parameterName, HttpRequest request, MethodLogger log, bool ConvertToLower = true)
    {
        // Obtain the parameter from the caller
        if (request.Query.TryGetValue(parameterName, out StringValues items))
        {
            if (items.Count > 1)
            {
                throw new UserErrorException($"Multiple {parameterName} found");
            }

            log.SetAttribute($"request.{parameterName}", items[0]);
        }
        else
        {
            throw new UserErrorException($"No {parameterName} found");
        }

        if (ConvertToLower)
        {
            return items[0].ToLowerInvariant();
        }
        else
        {
            return items[0];
        }
    }

    public async Task DefaultDelegate(HttpContext context)
    {
        // "using" is a C# system to ensure that the object is disposed of properly
        // when the block is exited. In this case, it will call the Dispose method
        using (var log = _logger.StartMethod(nameof(DefaultDelegate), context))
        {
            try
            {
                // Generally, a 200 OK is returned if the service is alive
                // and that is all that the load balancer needs, but a
                // text message can be useful for humans.
                // However, in some cases, the LB will be able to process more
                // health information to know how to react to your service, so
                // don't be surprised if you see code with more involved health 
                // checks.
                await context.Response.WriteAsync("Default for ml-prompt-handler = " + QuickHash("Default for ml-prompt-handler = "));
            }
            catch (Exception e)
            {
                // While you can just throw the exception back to the web server,
                // it is not recommended. It is better to catch the exception and
                // log it, then return a 500 Internal Server Error to the caller yourself.
                log.HandleException(e);
            }
        }
    }

    // Health Checks (aka ping) methods are handy to have on your service
    // They allow you to report that your are alive and return any other
    // information that is useful. These are often used by load balancers
    // to decide whether to send you traffic. For example, if you need a long
    // time to initialize, you can report that you are not ready yet.
    public async Task HealthCheckDelegate(HttpContext context)
    {
        // "using" is a C# system to ensure that the object is disposed of properly
        // when the block is exited. In this case, it will call the Dispose method
        using (var log = _logger.StartMethod(nameof(HealthCheckDelegate), context))
        {
            try
            {
                // Generally, a 200 OK is returned if the service is alive
                // and that is all that the load balancer needs, but a
                // text message can be useful for humans.
                // However, in some cases, the LB will be able to process more
                // health information to know how to react to your service, so
                // don't be surprised if you see code with more involved health 
                // checks.
                await context.Response.WriteAsync("Alive");
            }
            catch (Exception e)
            {
                // While you can just throw the exception back to the web server,
                // it is not recommended. It is better to catch the exception and
                // log it, then return a 500 Internal Server Error to the caller yourself.
                log.HandleException(e);
            }
        }
    }

    public async Task UploadPromptDelegate(HttpContext context)
    {
        using (var log = _logger.StartMethod(nameof(UploadPromptDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                IFormFile fileContent = context.Request.Form.Files.FirstOrDefault();
                if (fileContent == null)
                {
                    throw new UserErrorException("No file content found");
                }

                PromptMetadata m = new PromptMetadata();
                m.prompttype = GetParameterFromList("prompttype", request, log);
                m.promptname = fileContent.FileName;
                m.contenttype = fileContent.ContentType;
                m.contentlength = fileContent.Length;

                //m.promptname = Path.ChangeExtension(Path.GetFileNameWithoutExtension(m.promptname), Path.GetExtension(m.promptname).ToLowerInvariant());
                m.promptname = Path.GetFileNameWithoutExtension(m.promptname).ToLowerInvariant();
                m.prompttype = m.prompttype.ToLowerInvariant();
                m.timestamp = DateTime.UtcNow.ToString("o");

                log.SetAttribute("request.promptname", m.promptname);
                log.SetAttribute("request.contenttype", m.contenttype);
                log.SetAttribute("request.contentlength", m.contentlength);
                log.SetAttribute("request.timestamp", m.timestamp);

                // First step is we will write the metadata to CosmosDB
                // Here we are using Type mapping to convert our data structure
                // to a JSON document that can be stored in CosmosDB.
                if (await _cosmosDbWrapper.GetItemAsync<PromptMetadata>(m.id, m.prompttype) != null)
                {
                    await _cosmosDbWrapper.UpdateItemAsync(m.id, m.prompttype, m);
                }
                else
                {
                    await _cosmosDbWrapper.AddItemAsync(m, m.prompttype);
                }

                // Now we write the file into a blob storage element within the container.
                // We will use one container per user to keep things organized.
                var blobStorage = new BlobStorageWrapper(_configuration);
                using (var streamReader = new StreamReader(fileContent.OpenReadStream()))
                {
                    await blobStorage.WriteBlob(m.prompttype, m.promptname, streamReader.BaseStream);
                }

                // The POST has no response body, so we just return and the system
                // will return a 200 OK to the caller.
            }
            catch (UserErrorException e)
            {
                log.LogUserError(e.Message);
            }
            catch (Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task GetPromptDelegate(HttpContext context)
    {
        using (var log = _logger.StartMethod(nameof(GetPromptDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                PromptMetadata m = new PromptMetadata();
                m.prompttype = GetParameterFromList("prompttype", request, log);
                m.promptname = GetParameterFromList("promptname", request, log);

                m.promptname = Path.GetFileNameWithoutExtension(m.promptname).ToLowerInvariant();

                log.SetAttribute("request.promptname", m.promptname);

                // TODO: Implement the download file delegate to return the file
                // contents to the caller via the HTTP response after receiving both
                // the prompttype and the promptname to find.

                HttpResponse response = context.Response;
                //If this fails, should throw a UserErrorException FileNotFound (404)
                m = await _cosmosDbWrapper.GetItemAsync<PromptMetadata>(m.id, m.prompttype);
                if (m == null)
                {
                    throw new UserErrorException();
                }
                


                response.Headers.Append("Content-Disposition", $"attachment; filename=\"{m.id}\"");

                var blobStorage = new BlobStorageWrapper(_configuration);
                
                using var stream = new MemoryStream();
                    await blobStorage.DownloadBlob(m.prompttype, m.promptname, stream);
                    stream.Position = 0;

                    using var streamreader = new StreamReader(stream);
                    string blobdata = await streamreader.ReadToEndAsync();
                    var responsedata = new {PromptName=m.promptname, PromptData=blobdata};

                await response.WriteAsJsonAsync(responsedata);

                log.SetAttribute("response.contenttype", response.ContentType);
                log.SetAttribute("response.contentlength", response.ContentLength);
                log.SetAttribute("response.content", response.Body);
            }
            catch (UserErrorException e)
            {
                log.LogUserError(e.Message);
            }
            catch (Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task FindPromptMetadataDelegate(HttpContext context)
    {
        using (var log = _logger.StartMethod(nameof(FindPromptMetadataDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                PromptMetadata m = new PromptMetadata();
                m.prompttype = GetParameterFromList("prompttype", request, log);
                m.timestamp = GetParameterFromList("timestamp", request, log, false);

                // TODO: Implement the list files delegate to return a list of files
                // that are associated with the prompttype provided in the HTTP request.
                HttpResponse response = context.Response;
                string query = $"SELECT TOP 1 * FROM c WHERE c.prompttype = \"{m.prompttype}\" AND c.timestamp > \"{m.timestamp}\" ORDER BY c.timestamp ASC";
                IEnumerable<PromptMetadata> metadatas = await _cosmosDbWrapper.GetItemsAsync<PromptMetadata>(query);
                if (metadatas == null)
                {
                    throw new UserErrorException("No New Prompt Found");
                }
                PromptMetadata nextMetadata = metadatas.First();

                await context.Response.WriteAsJsonAsync(nextMetadata);

                log.SetAttribute("response.contenttype", response.ContentType);
                //log.SetAttribute("response.contentlength", response.ContentLength);//I get the sense that WriteAsJsonAsync not automatically setting ContentLength means it's not necessary.
                log.SetAttribute("response.content", response.Body);
            }
            catch (UserErrorException e)
            {
                log.LogUserError(e.Message);
            }
            catch (Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task ListPromptsDelegate(HttpContext context)
    {
        using (var log = _logger.StartMethod(nameof(ListPromptsDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                PromptMetadata m = new PromptMetadata();
                m.prompttype = GetParameterFromList("prompttype", request, log);

                // TODO: Implement the list files delegate to return a list of files
                // that are associated with the prompttype provided in the HTTP request.
                HttpResponse response = context.Response;
                string query = $"SELECT * FROM c WHERE c.prompttype = \"{m.prompttype}\"";
                IEnumerable<PromptMetadata> metadatas = await _cosmosDbWrapper.GetItemsAsync<PromptMetadata>(query);
                if (metadatas == null)
                {
                    throw new UserErrorException();
                }

                //What magery is this!? I think I'll have to look into "Select" syntax in the future.
                List<string> promptnames = metadatas.Select(p => p.promptname).ToList();

                await context.Response.WriteAsJsonAsync(promptnames);

                log.SetAttribute("response.contenttype", response.ContentType);
                //log.SetAttribute("response.contentlength", response.ContentLength);//I get the sense that WriteAsJsonAsync not automatically setting ContentLength means it's not necessary.
                log.SetAttribute("response.content", response.Body);
            }
            catch (UserErrorException e)
            {
                log.LogUserError(e.Message);
            }
            catch (Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task GetAllPromptsDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(GetAllPromptsDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                string prompttype = GetParameterFromList("prompttype", request, log);

                // TODO: Implement the list files delegate to return a list of files
                // that are associated with the userId provided in the HTTP request.
                HttpResponse response = context.Response;
                string query = $"SELECT * FROM c WHERE c.prompttype = \"{prompttype}\"";
                IEnumerable<PromptMetadata> metadatas = await _cosmosDbWrapper.GetItemsAsync<PromptMetadata>(query);
                if (metadatas == null)
                {
                    throw new UserErrorException();
                }
                
                response.Headers.Append("Content-Disposition", $"attachment; filename=\"{prompttype}_prompts.json\"");

                var responses = new List<object>();
                var blobStorage = new BlobStorageWrapper(_configuration);
                foreach (var metadata in metadatas)
                {
                    using var stream = new MemoryStream();
                    await blobStorage.DownloadBlob(metadata.prompttype, metadata.promptname, stream);
                    stream.Position = 0;

                    using var streamreader = new StreamReader(stream);
                    string blobdata = await streamreader.ReadToEndAsync();
                    responses.Add(new {PromptName=metadata.promptname, PromptData=blobdata});
                }

                await response.WriteAsJsonAsync(responses);

                // log.SetAttribute("response.contenttype", response.ContentType);
                // log.SetAttribute("response.contentlength", response.ContentLength);
                // log.SetAttribute("response.content", response.Body);
            }
            catch (UserErrorException e)
            {
                log.LogUserError(e.Message);
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task DeletePromptDelegate(HttpContext context)
    {
        using (var log = _logger.StartMethod(nameof(DeletePromptDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                PromptMetadata m = new PromptMetadata();
                m.prompttype = GetParameterFromList("prompttype", request, log);
                m.promptname = GetParameterFromList("promptname", request, log);

                // TODO: Implement the delete file delegate to remove the file
                // from the storage system and the metadata from the CosmosDB database.
                //Failure to find the file to be deleted will be logged, but not considered a failure state.
                //I don't know what would cause "Terminal Failure" to show, but I know it would indeed be terminal, so that's what the default value gets to be.
                string deletionStatus = "Terminal Failure";
                if (await _cosmosDbWrapper.GetItemAsync<PromptMetadata>(m.id, m.prompttype) != null)
                {
                    await _cosmosDbWrapper.DeleteItemAsync(m.id, m.prompttype);
                    deletionStatus = "Prompt Found And Deleted";
                }
                else
                {
                    deletionStatus = "Prompt Not Found";

                }
                log.SetAttribute("deletion.status", deletionStatus);

                var blobStorage = new BlobStorageWrapper(_configuration);
                await blobStorage.DeleteBlob(m.prompttype, m.id);

                string returnString = deletionStatus + ": " + m.id;

                HttpResponse response = context.Response;
                response.StatusCode = 200;
                response.ContentLength = Encoding.UTF8.GetByteCount(returnString);
                response.ContentType = "text/plain; charset=utf-8";

                await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
                {
                    await bodyWriter.WriteAsync(returnString);
                    await bodyWriter.FlushAsync();
                }
            }
            catch (Exception e)
            {
                log.HandleException(e);
            }
        }
    }
}
