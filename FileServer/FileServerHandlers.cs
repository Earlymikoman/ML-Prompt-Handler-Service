
using AzureFileServer.Azure;
using AzureFileServer.Utils;
using Microsoft.Extensions.Primitives;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Security.Cryptography;

namespace AzureFileServer.FileServer;

// This is the core logic of the web server and hosts all of the HTTP
// handlers used by the web server regarding File Server functionality.
public class FileServerHandlers
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

    public FileServerHandlers(IConfiguration configuration)
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

    private static string GetParameterFromList(string parameterName, HttpRequest request, MethodLogger log)
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

        return items[0];
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

                log.SetAttribute("request.promptname", m.promptname);
                log.SetAttribute("request.contenttype", m.contenttype);
                log.SetAttribute("request.contentlength", m.contentlength);

                // First step is we will write the metadata to CosmosDB
                // Here we are using Type mapping to convert our data structure
                // to a JSON document that can be stored in CosmosDB.
                if (await _cosmosDbWrapper.GetItemAsync<PromptMetadata>(m.promptname, m.prompttype) != null)
                {
                    await _cosmosDbWrapper.UpdateItemAsync(m.promptname, m.prompttype, m);
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
                m.promptname = GetParameterFromList("filename", request, log);

                m.promptname = Path.ChangeExtension(Path.GetFileNameWithoutExtension(m.promptname), Path.GetExtension(m.promptname).ToLowerInvariant());

                log.SetAttribute("request.promptname", m.promptname);

                // TODO: Implement the download file delegate to return the file
                // contents to the caller via the HTTP response after receiving both
                // the prompttype and the filename to find.

                HttpResponse response = context.Response;
                //If this fails, should throw a UserErrorException FileNotFound (404)
                m = await _cosmosDbWrapper.GetItemAsync<PromptMetadata>(m.promptname, m.prompttype);
                if (m == null)
                {
                    throw new UserErrorException();
                }
                response.ContentType = m.contenttype;
                response.ContentLength = m.contentlength;
                //I wasn't sure if printing to the page was sufficient, or if it should be an actual download;
                //Went with actual download because uploadfile seems to deal in actual files, so downloadfile should too.
                //Full disclosure, this line in particular is just AI (Grok); I asked it how to download a file
                //via http rather than just print the response, and this was the result.
                response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(m.promptname)}\"");

                var blobStorage = new BlobStorageWrapper(_configuration);
                await blobStorage.DownloadBlob(m.prompttype, m.promptname, response.Body);

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

                string fileStrings = metadatas.Count() + " Files Found:\n";
                foreach (PromptMetadata metadata in metadatas)
                {
                    fileStrings += "\t" + metadata.ToString() + "\n";
                }
                response.StatusCode = 200;
                response.ContentLength = Encoding.UTF8.GetByteCount(fileStrings);
                response.ContentType = "text/plain; charset=utf-8";

                await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
                {
                    await bodyWriter.WriteAsync(fileStrings);
                    await bodyWriter.FlushAsync();
                }

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

    public async Task DeletePromptDelegate(HttpContext context)
    {
        using (var log = _logger.StartMethod(nameof(DeletePromptDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                PromptMetadata m = new PromptMetadata();
                m.prompttype = GetParameterFromList("prompttype", request, log);
                m.promptname = GetParameterFromList("filename", request, log);

                m.promptname = Path.ChangeExtension(Path.GetFileNameWithoutExtension(m.promptname), Path.GetExtension(m.promptname).ToLowerInvariant());

                // TODO: Implement the delete file delegate to remove the file
                // from the storage system and the metadata from the CosmosDB database.
                //Failure to find the file to be deleted will be logged, but not considered a failure state.
                //I don't know what would cause "Terminal Failure" to show, but I know it would indeed be terminal, so that's what the default value gets to be.
                string deletionStatus = "Terminal Failure";
                if (await _cosmosDbWrapper.GetItemAsync<PromptMetadata>(m.promptname, m.prompttype) != null)
                {
                    await _cosmosDbWrapper.DeleteItemAsync(m.promptname, m.prompttype);
                    deletionStatus = "File Found And Deleted";
                }
                else
                {
                    deletionStatus = "File Not Found";

                }
                log.SetAttribute("deletion.status", deletionStatus);

                var blobStorage = new BlobStorageWrapper(_configuration);
                await blobStorage.DeleteBlob(m.prompttype, m.promptname);

                HttpResponse response = context.Response;
                response.StatusCode = 200;
                response.ContentLength = Encoding.UTF8.GetByteCount(deletionStatus + ": " + m.promptname);
                response.ContentType = "text/plain; charset=utf-8";

                await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
                {
                    await bodyWriter.WriteAsync(deletionStatus + ": " + m.promptname);
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