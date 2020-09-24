using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.CognitiveServices.Knowledge.QnAMaker;
using Microsoft.Azure.CognitiveServices.Knowledge.QnAMaker.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Rest;

namespace QnAIntegration
{
    public static class QnAIntegrationCustomSkill
    {
        // TODO: instruct people how to set these before publishing. Move to config.   
        private static string authoringKey = "REPLACE-WITH-YOUR-QNA-MAKER-KEY";
        private static string resourceName = "REPLACE-WITH-YOUR-RESOURCE-NAME";
        // TODO: create the knowledge base at https://www.qnamaker.ai/
        private static string knowledgeBaseID = "REPLACE-WITH-YOUR-KNOWLEDGE-BASE-ID";

        private static string authoringURL = $"https://{resourceName}.cognitiveservices.azure.com";

        
        [FunctionName("QnAIntegrationCustomSkill")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("QnAIntegrationCustomSkill has started a request.");

            var response = new WebApiResponse
            {
                Values = new List<OutputRecord>()
            };

            string requestBody = new StreamReader(req.Body).ReadToEnd();
            var data = JsonConvert.DeserializeObject<WebApiRequest>(requestBody);
            log.LogInformation("We deserialized the request ok: " + requestBody);

            // Do some schema validation
            if (data == null)
            {
                return new BadRequestObjectResult("The request schema does not match expected schema.");
            }
            if (data.Values == null)
            {
                return new BadRequestObjectResult("The request schema does not match expected schema. Could not find values array.");
            }

            // Initializations  
            QnAMakerClient qnAMakerClient = new QnAMakerClient(new ApiKeyServiceClientCredentials(authoringKey))
            {
                Endpoint = authoringURL
            };
            List<FileDTO> fileList = new List<FileDTO>();
            List<string> urlList = new List<string>();
            Dictionary<string, int> langDict = new Dictionary<string, int>();
            log.LogInformation("Everything initialized.");

            // Compose the response for each value.
            foreach (var record in data.Values)
            {
                if (record == null || record.RecordId == null) continue;

                OutputRecord responseRecord = new OutputRecord
                {
                    RecordId = record.RecordId,
                    Data = new OutputRecord.OutputRecordData(),
                    Errors = new List<OutputRecord.OutputRecordMessage>(),
                    Warnings = new List<OutputRecord.OutputRecordMessage>()
                };

                try
                {
                    log.LogInformation("RecordID = " + record.RecordId + ", filename = " + record.Data.FileName + ", fileUri = " + record.Data.FileUri + ", lang = " + record.Data.Language);

                    // TODO: need stronger logic on when to use URL vs. File - test more
                    if (record.Data.FileName.EndsWith("html"))
                    {
                        log.LogInformation("Adding a URL");
                        urlList.Add(record.Data.FileUri);
                    }
                    else
                    {
                        // Record file
                        log.LogInformation("Adding a file");
                        var fileDTO = new FileDTO
                        {
                            FileName = record.Data.FileName,
                            FileUri = record.Data.FileUri
                        };
                        fileList.Add(fileDTO);
                    }

                    // Record language (we will choose the most common language at the end)
                    // TODO: change this logic to use a QnA service per language - see
                    // https://docs.microsoft.com/azure/cognitive-services/QnAMaker/concepts/design-language-culture
                    if (langDict.ContainsKey(record.Data.Language))
                    {
                        int currentValue = Convert.ToInt32(langDict[record.Data.Language]);
                        langDict[record.Data.Language] = currentValue + 1;
                    }
                    else
                    {
                        langDict.Add(record.Data.Language, 1);
                    }
                    log.LogInformation("State of langDict: " + string.Join(", ", langDict.Select(kvp => $"{kvp.Key}: {kvp.Value}")));

                    // No output is really needed, so repeating input back.  
                    responseRecord.Data.Message = "Success";
                    responseRecord.Data.FileName = record.Data.FileName;
                    responseRecord.Data.FileUri = record.Data.FileUri;
                    responseRecord.Data.Language = record.Data.Language;
                    log.LogInformation("Done writing output record");
                }
                catch (Exception e)
                {
                    log.LogError(e, e.Message);
                    if (e.InnerException != null)
                    {
                        log.LogError(e.InnerException.Message);
                    }

                    // Something bad happened; log the issue.
                    var error = new OutputRecord.OutputRecordMessage
                    {
                        Message = e.Message
                    };

                    responseRecord.Errors.Add(error);
                }
                finally
                {
                    response.Values.Add(responseRecord);
                }
            }

            int maxLangCount = langDict.Values.Max();
            string mostCommonLanguage = langDict.FirstOrDefault(x => x.Value == maxLangCount).Key;
            log.LogInformation("The most common language is " + mostCommonLanguage);

            try
            {
                // I originally wrote this thinking that we would create the knowledge 
                // base in this custom skill, but we'd be better off if the user creates
                // it outside of here.  If we do it in this function, we would need to 
                // take a lock and slow down the parallelism.  Parallel instances
                // of this Azure function scaling out would need to use the same knowledge 
                // base.  Later indexer runs also need to know the same knowledgeBaseID
                // to get the existing knowledge base rather than creating a new one.  
                // So creating once upfront makes sense.  
                // Since this iteration of the code is setting the knowledgeBaseID upfront,
                // this means that we should always fall into the "else" block below.  
                // TODO: if we end up staying with this strategy, remove the "if" code path.  
                if (String.IsNullOrEmpty(knowledgeBaseID))
                {
                    log.LogInformation("Creating kb with {0} files and {1} urls", fileList.Count.ToString(), urlList.Count.ToString());
                    knowledgeBaseID = await CreateKb(qnAMakerClient, fileList, urlList, mostCommonLanguage);
                    log.LogInformation("KnowledgeBaseID is " + knowledgeBaseID.ToString());
                }
                else
                {
                    log.LogInformation("Updating kb with {0} files and {1} urls", fileList.Count.ToString(), urlList.Count.ToString());
                    await UpdateKB(qnAMakerClient, knowledgeBaseID, fileList, urlList);
                }
                log.LogInformation("About to publish");
                await PublishKb(qnAMakerClient, knowledgeBaseID);
                log.LogInformation("Published kb!");
            }
            catch (Exception e)
            {
                log.LogError(e, e.Message);
                if (e.InnerException != null)
                {
                    log.LogError("Inner exception: " + e.InnerException.Message);
                }
            }

            return new OkObjectResult(response);
        }

        #region Class used to deserialize the request
        private class InputRecord
        {
            public class InputRecordData
            {
                public string FileName { get; set; }
                public string FileUri { get; set; }
                public string Language { get; set; }
            }

            public string RecordId { get; set; }
            public InputRecordData Data { get; set; }
        }

        private class WebApiRequest
        {
            public List<InputRecord> Values { get; set; }
        }
        #endregion

        #region Classes used to serialize the response

        private class OutputRecord
        {
            public class OutputRecordData
            {
                public string Message { get; set; } = "";
                public string FileName { get; set; }
                public string FileUri { get; set; }
                public string Language { get; set; }
            }

            public class OutputRecordMessage
            {
                public string Message { get; set; }
            }

            public string RecordId { get; set; }
            public OutputRecordData Data { get; set; }
            public List<OutputRecordMessage> Errors { get; set; }
            public List<OutputRecordMessage> Warnings { get; set; }
        }

        private class WebApiResponse
        {
            public List<OutputRecord> Values { get; set; }
        }
        #endregion

        #region QnA Maker helper methods
        private static async Task<Operation> MonitorOperation(IQnAMakerClient client, Operation operation)
        {
            // Loop while operation is success
            for (int i = 0;
                i < 20 && (operation.OperationState == OperationStateType.NotStarted || operation.OperationState == OperationStateType.Running);
                i++)
            {
                Console.WriteLine("Waiting for operation: {0} to complete.", operation.OperationId);
                await Task.Delay(5000);
                operation = await client.Operations.GetDetailsAsync(operation.OperationId);
            }

            if (operation.OperationState != OperationStateType.Succeeded)
            {
                ErrorResponseError ere = operation.ErrorResponse.Error;
                // TODO: can look in ere.Details and ere.InnerError if needed - log that?
                //throw new Exception($"Operation {operation.OperationId} failed to complete: {operation.ErrorResponse}");
                throw new Exception($"Operation {operation.OperationId} failed to complete: code {ere.Code}, message: {ere.Message}, target: {ere.Target}");
            }
            return operation;
        }

        private static async Task<string> CreateKb(IQnAMakerClient qnAMakerClient, List<FileDTO> fileList, List<string> urlList, string language)
        {
            var createKbDto = new CreateKbDTO
            {
                Name = "Cognitive Search QnA Maker Integration KB",
                Files = fileList,
                Urls = urlList,
                Language = language
                //DefaultAnswerUsedForExtraction = ""
                // TODO: add default?  
            };

            var createOp = await qnAMakerClient.Knowledgebase.CreateAsync(createKbDto);
            createOp = await MonitorOperation(qnAMakerClient, createOp);
            return createOp.ResourceLocation.Replace("/knowledgebases/", string.Empty);
        }

        private static async Task UpdateKB(IQnAMakerClient client, string kbId, List<FileDTO> fileList, List<string> urlList)
        {
            var updateOp = await client.Knowledgebase.UpdateAsync(kbId, new UpdateKbOperationDTO
            {
                // Create JSON of changes
                Add = new UpdateKbOperationDTOAdd
                {
                    Files = fileList,
                    Urls = urlList
                },
                Update = null,
                Delete = null
            });
            // TODO: should this be update instead of add?  How does that work?  
            // We want to update when the source file/URL is the same.  

            // Loop while operation is success
            updateOp = await MonitorOperation(client, updateOp);
        }

        private static async Task PublishKb(IQnAMakerClient client, string kbId)
        {
            await client.Knowledgebase.PublishAsync(kbId);
        }

        #endregion
    }
}

