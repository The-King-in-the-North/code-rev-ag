using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Agent.Abhay
{
    public class myagent_test
    {
        private readonly ILogger<myagent_test> _logger;

        public myagent_test(ILogger<myagent_test> logger)
        {
            _logger = logger;
        }

        [Function("myagent_test")]
        public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");
            int a = 10/0;
            return new OkObjectResult("Welcome to Azure Functions!");
        }
    }
}
