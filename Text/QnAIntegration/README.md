---
page_type: sample
languages:
- csharp
products:
- azure
- azure-search
name: QnA Integration sample skill for cognitive search
description: This custom skill sends ingested data to a [QnA Maker](https://www.qnamaker.ai/) knowledge base.
azureDeploy: https://raw.githubusercontent.com/Azure-Samples/azure-search-power-skills/master/Text/QnAIntegration/azuredeploy.json
---

# QnA Integration

This custom skill sends ingested data to a [QnA Maker](https://www.qnamaker.ai/) knowledge base.

TODO: add "deploy to Azure" functionality
[![Deploy to Azure](https://azuredeploy.net/deploybutton.svg)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2FAzure-Samples%2Fazure-search-power-skills%2Fmaster%2FText%2FQnAIntegration%2Fazuredeploy.json)

## Requirements

This skill has no additional requirements than the ones described in [the root `README.md` file](../../README.md).

## Settings

TODO: cover how to set up knowledge base if we want them to do that outside the service,
and where to put the settings (QnA service resource name, authoring key, etc).

### Sample Input:

```json
{
    "values": [
        {
            "recordId":"0",
            "data": 
            {
                "FileName":"About the Pandemic Community Advisory Group - King County.html",
                "FileUri":"https://kingcountycovidjen.blob.core.windows.net/website/About%20the%20Pandemic%20Community%20Advisory%20Group%20-%20King%20County.html",
                "Language":"en"
            }
        }
    ]
}
```

### Sample Output:
For QnA data ingestion, there are no outputs needed at ingestion time, so the current output
mirrors the input closely (TODO: can change this to just message after debugging).

```json
{
    "values": [
        {
            "recordId": "0",
            "data": 
            {
                "message": "Success",
                "fileName": "About the Pandemic Community Advisory Group - King County.html",
                "fileUri": "https://kingcountycovidjen.blob.core.windows.net/website/About%20the%20Pandemic%20Community%20Advisory%20Group%20-%20King%20County.html",
                "language": "en"
            },
            "errors": [],
            "warnings": []
        }
    ]
}
```

## Sample Skillset Integration

In order to use this skill in a cognitive search pipeline, you'll need to add a skill definition to your skillset.
Here's a sample skill definition for this example (inputs and outputs should be updated to reflect your particular scenario and skillset environment):

```json
{
      "@odata.type": "#Microsoft.Skills.Custom.WebApiSkill",
      "name": "QnA Integration",
      "description": "Custom skill to create a QnA knowledge base",
      "context": "/document",
      "uri": "[AzureFunctionEndpointUrl]/api/QnAIntegrationCustomSkill?code=[AzureFunctionDefaultHostKey]",
      "batchSize": 10,
      "degreeOfParallelism": 1,
      "inputs": [
        {
          "name": "FileName",
          "source": "/document/metadata_storage_name"
        },
        {
          "name": "FileUri",
          "source": "/document/fileUri"
        },
        {
          "name": "Language",
          "source": "/document/language"
        }
      ],
      "outputs": [
        {
          "name": "Message",
          "targetName": "/document/message"
        }
      ]
}
```
