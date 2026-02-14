
using AzureFileServer.Azure;
using AzureFileServer.Utils;
using Microsoft.Extensions.Primitives;
using System.Text.Json;

namespace AzureFileServer.FileServer;

// This is the core logic of the web server and hosts all of the HTTP
// handlers used by the web server regarding File Server functionality.
public class FileServerHandlers
{
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
        using(var log = _logger.StartMethod(nameof(DefaultDelegate), context))
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
                await context.Response.WriteAsync("Default");
            }
            catch(Exception e)
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
        using(var log = _logger.StartMethod(nameof(HealthCheckDelegate), context))
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
            catch(Exception e)
            {
                // While you can just throw the exception back to the web server,
                // it is not recommended. It is better to catch the exception and
                // log it, then return a 500 Internal Server Error to the caller yourself.
                log.HandleException(e);
            }
        }
    }

    public async Task UploadFileDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(UploadFileDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                IFormFile fileContent = context.Request.Form.Files.FirstOrDefault();
                if (fileContent == null)
                {
                    throw new UserErrorException("No file content found");
                }

                FileMetadata m = new FileMetadata();
                m.userid = GetParameterFromList("userid", request, log);
                m.filename = fileContent.FileName;
                m.contenttype = fileContent.ContentType;
                m.contentlength = fileContent.Length;                

                log.SetAttribute("request.filename", fileContent.FileName);
                log.SetAttribute("request.contenttype", fileContent.ContentType);
                log.SetAttribute("request.contentlength", fileContent.Length);

                // First step is we will write the metadata to CosmosDB
                // Here we are using Type mapping to convert our data structure
                // to a JSON document that can be stored in CosmosDB.
                if (await _cosmosDbWrapper.GetItemAsync<FileMetadata>(m.id, m.userid) != null)
                {
                    await _cosmosDbWrapper.UpdateItemAsync(m.id, m.userid, m);
                }
                else
                {
                    await _cosmosDbWrapper.AddItemAsync(m, m.userid);
                }

                // Now we write the file into a blob storage element within the container.
                // We will use one container per user to keep things organized.
                var blobStorage = new BlobStorageWrapper(_configuration);
                using (var streamReader = new StreamReader(fileContent.OpenReadStream()))
                {
                    await blobStorage.WriteBlob(m.userid, m.filename, streamReader.BaseStream);
                }

                // The POST has no response body, so we just return and the system
                // will return a 200 OK to the caller.
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

    public async Task DownloadFileDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(DownloadFileDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                FileMetadata m = new FileMetadata();
                m.userid = GetParameterFromList("userid", request, log);
                m.filename = GetParameterFromList("filename", request, log);

                log.SetAttribute("request.filename", m.filename);

                // TODO: Implement the download file delegate to return the file
                // contents to the caller via the HTTP response after receiving both
                // the userId and the filename to find.

                HttpResponse response = context.Response;
                //If this fails, should throw a UserErrorException FileNotFound (404)
                m = await _cosmosDbWrapper.GetItemAsync<FileMetadata>(m.id, m.userid);
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
                response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(m.filename)}\"");

                var blobStorage = new BlobStorageWrapper(_configuration);
                await blobStorage.DownloadBlob(m.userid, m.filename, response.Body);

                log.SetAttribute("response.contenttype", response.ContentType);
                log.SetAttribute("response.contentlength", response.ContentLength);
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

    public async Task ListFilesDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(ListFilesDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                FileMetadata m = new FileMetadata();
                m.userid = GetParameterFromList("userid", request, log);

                // TODO: Implement the list files delegate to return a list of files
                // that are associated with the userId provided in the HTTP request.
                HttpResponse response = context.Response;
                IEnumerable<FileMetadata> metadatas = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(m.userid);
                
                string fileStrings = "";
                foreach (FileMetadata metadata in metadatas)
                {
                    fileStrings += metadata.ToString() + "\n";
                }
                response.ContentLength = fileStrings.Length;
                response.ContentType = "text/plain";

                await using (var bodyWriter = new StreamWriter(response.Body))
                {
                    await bodyWriter.WriteAsync(fileStrings);
                    await bodyWriter.FlushAsync();
                }
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task DeleteFileDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(DeleteFileDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                FileMetadata m = new FileMetadata();
                m.userid = GetParameterFromList("userid", request, log);
                m.filename = GetParameterFromList("filename", request, log);

                // TODO: Implement the delete file delegate to remove the file
                // from the storage system and the metadata from the CosmosDB database.
                throw new NotImplementedException();
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }
}