namespace OJS.Workers.SubmissionProcessors.Models
{
    public class ExceptionModel
    {
        public string Message { get; set; }

        public string StackTrace { get; set; }

        public string ExceptionType { get; set; }
    }
}