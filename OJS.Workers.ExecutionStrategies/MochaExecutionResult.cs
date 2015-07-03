﻿namespace OJS.Workers.ExecutionStrategies
{
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public class MochaExecutionResult
    {
        public bool Passed { get; set; }

        public string Error { get; set; }

        public static MochaExecutionResult Parse(string result)
        {
            JObject jsonTestResult = null;
            var passed = false;
            string error = null;

            try
            {
                jsonTestResult = JObject.Parse(result.Trim().Replace("/*", string.Empty).Replace("*/", string.Empty));
                passed = (int)jsonTestResult["stats"]["passes"] == 1;
            }
            catch
            {
                error = "Invalid console output!";
            }

            if (!passed)
            {
                try
                {
                    error = (string)jsonTestResult["failures"][0]["err"]["message"];
                }
                catch
                {
                    error = "Invalid console output!";
                }
            }

            return new MochaExecutionResult
            {
                Passed = passed,
                Error = error
            };
        }
    }
}
