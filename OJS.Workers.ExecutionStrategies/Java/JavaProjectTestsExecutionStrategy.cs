﻿namespace OJS.Workers.ExecutionStrategies.Java
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using OJS.Workers.Common;
    using OJS.Workers.Common.Helpers;
    using OJS.Workers.Common.Models;
    using OJS.Workers.ExecutionStrategies.Helpers;
    using OJS.Workers.ExecutionStrategies.Models;
    using OJS.Workers.Executors;

    public class JavaProjectTestsExecutionStrategy : JavaUnitTestsExecutionStrategy
    {
        public JavaProjectTestsExecutionStrategy(
            Func<CompilerType, string> getCompilerPathFunc,
            IProcessExecutorFactory processExecutorFactory,
            string javaExecutablePath,
            string javaLibrariesPath,
            int baseTimeUsed,
            int baseMemoryUsed)
            : base(
                getCompilerPathFunc,
                processExecutorFactory,
                javaExecutablePath,
                javaLibrariesPath,
                baseTimeUsed,
                baseMemoryUsed) =>
                    this.UserClassNames = new List<string>();

        protected List<string> UserClassNames { get; }

        protected override string ClassPath => $@" -classpath ""{this.WorkingDirectory};{this.JavaLibrariesPath}*""";

        protected override string JUnitTestRunnerCode
        {
            get => $@"
import org.junit.runner.JUnitCore;
import org.junit.runner.Result;
import org.junit.runner.notification.Failure;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.PrintStream;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

public class _$TestRunner {{
    public static void main(String[] args) {{
        for (String arg: args) {{
            try {{
                Class currentClass = Class.forName(arg);
                Classes.allClasses.put(currentClass.getSimpleName(),currentClass);
            }} catch (ClassNotFoundException e) {{}}
        }}

        Class[] testClasses = new Class[]{{{string.Join(", ", this.TestNames.Select(x => x + ".class"))}}};

        InputStream originalIn = System.in;
        PrintStream originalOut = System.out;

        InputStream in = new ByteArrayInputStream(new byte[0]);
        System.setIn(in);

        ByteArrayOutputStream out = new ByteArrayOutputStream();
        System.setOut(new PrintStream(out));

        List<Result> results = new ArrayList<>();
        for (Class testClass: testClasses) {{
            results.add(JUnitCore.runClasses(testClass));
        }}

        System.setIn(originalIn);
        System.setOut(originalOut);

        for (Result result : results){{
            for (Failure failure : result.getFailures()) {{
                String failureClass = failure.getDescription().getTestClass().getSimpleName();
                String failureException = failure.getException().toString().replaceAll(""\r"", ""\\\\r"").replaceAll(""\n"",""\\\\n"");
                System.out.printf(""%s %s%s"", failureClass, failureException, System.lineSeparator());
            }}
        }}
    }}
}}

class Classes{{
    public static Map<String, Class> allClasses = new HashMap<>();
}}";
        }

        protected override IExecutionResult<TestResult> ExecuteAgainstTestsInput(
            IExecutionContext<TestsInputModel> executionContext,
            IExecutionResult<TestResult> result)
        {
            // Create a temp file with the submission code
            string submissionFilePath;
            try
            {
                submissionFilePath = this.CreateSubmissionFile(executionContext);
            }
            catch (ArgumentException exception)
            {
                result.IsCompiledSuccessfully = false;
                result.CompilerComment = exception.Message;

                return result;
            }

            var compilerPath = this.GetCompilerPathFunc(executionContext.CompilerType);
            var combinedArguments = executionContext.AdditionalCompilerArguments + this.ClassPath;

            var executor = this.CreateExecutor(ProcessExecutorType.Restricted);

            if (!string.IsNullOrWhiteSpace(executionContext.Input.TaskSkeletonAsString))
            {
                FileHelpers.UnzipFile(submissionFilePath, this.WorkingDirectory);

                var className = JavaCodePreprocessorHelper
                    .GetPublicClassName(executionContext.Input.TaskSkeletonAsString);

                var filePath = $"{this.WorkingDirectory}\\{className}{Constants.JavaSourceFileExtension}";

                File.WriteAllText(filePath, executionContext.Input.TaskSkeletonAsString);
                FileHelpers.AddFilesToZipArchive(submissionFilePath, string.Empty, filePath);

                var preprocessCompileResult = this.Compile(
                    executionContext.CompilerType,
                    compilerPath,
                    combinedArguments,
                    submissionFilePath);

                result.IsCompiledSuccessfully = preprocessCompileResult.IsCompiledSuccessfully;
                result.CompilerComment = preprocessCompileResult.CompilerComment;
                if (!result.IsCompiledSuccessfully)
                {
                    return result;
                }

                var preprocessExecutor = this.CreateExecutor(ProcessExecutorType.Standard);

                var preprocessArguments = new List<string>();
                preprocessArguments.Add(this.ClassPath);
                preprocessArguments.Add(AdditionalExecutionArguments);
                preprocessArguments.Add(className);
                preprocessArguments.Add(this.WorkingDirectory);
                preprocessArguments.AddRange(this.UserClassNames);

                var preprocessExecutionResult = preprocessExecutor.Execute(
                    this.JavaExecutablePath,
                    string.Empty,
                    executionContext.TimeLimit,
                    executionContext.MemoryLimit,
                    preprocessArguments,
                    this.WorkingDirectory);

                JavaStrategiesHelper.ValidateJvmInitialization(preprocessExecutionResult.ReceivedOutput);

                var filesToAdd = preprocessExecutionResult
                    .ReceivedOutput
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var file in filesToAdd)
                {
                    var path = Path.GetDirectoryName(file);
                    FileHelpers.AddFilesToZipArchive(submissionFilePath, path, this.WorkingDirectory + "\\" + file);
                }

                File.Delete(filePath);
            }

            var compilerResult = this.Compile(
                executionContext.CompilerType,
                compilerPath,
                combinedArguments,
                submissionFilePath);

            result.IsCompiledSuccessfully = compilerResult.IsCompiledSuccessfully;
            result.CompilerComment = compilerResult.CompilerComment;
            if (!result.IsCompiledSuccessfully)
            {
                return result;
            }

            var arguments = new List<string>
            {
                this.ClassPath,
                AdditionalExecutionArguments,
                JUnitRunnerClassName
            };

            arguments.AddRange(this.UserClassNames);

            var processExecutionResult = executor.Execute(
                this.JavaExecutablePath,
                string.Empty,
                executionContext.TimeLimit,
                executionContext.MemoryLimit,
                arguments,
                this.WorkingDirectory,
                true);

            JavaStrategiesHelper.ValidateJvmInitialization(processExecutionResult.ReceivedOutput);

            var errorsByFiles = this.GetTestErrors(processExecutionResult.ReceivedOutput);
            var testIndex = 0;

            var checker = executionContext.Input.GetChecker();

            foreach (var test in executionContext.Input.Tests)
            {
                var message = "Test Passed!";
                var testFile = this.TestNames[testIndex++];
                if (errorsByFiles.ContainsKey(testFile))
                {
                    message = errorsByFiles[testFile];
                }

                var testResult = this.CheckAndGetTestResult(
                    test,
                    processExecutionResult,
                    checker,
                    message);

                result.Results.Add(testResult);
            }

            return result;
        }

        protected override string PrepareSubmissionFile(IExecutionContext<TestsInputModel> context)
        {
            var submissionFilePath = $"{this.WorkingDirectory}\\{SubmissionFileName}";
            File.WriteAllBytes(submissionFilePath, context.FileContent);
            FileHelpers.RemoveFilesFromZip(submissionFilePath, RemoveMacFolderPattern);
            this.ExtractUserClassNames(submissionFilePath);
            this.AddTestsToUserSubmission(context, submissionFilePath);
            this.AddTestRunnerTemplate(submissionFilePath);

            return submissionFilePath;
        }

        protected virtual void AddTestsToUserSubmission(
            IExecutionContext<TestsInputModel> context,
            string submissionZipFilePath)
        {
            var testNumber = 0;
            var filePaths = new string[context.Input.Tests.Count()];

            foreach (var test in context.Input.Tests)
            {
                var className = JavaCodePreprocessorHelper.GetPublicClassName(test.Input);
                var testFileName =
                    $"{this.WorkingDirectory}\\{className}{Constants.JavaSourceFileExtension}";

                File.WriteAllText(testFileName, test.Input);
                filePaths[testNumber] = testFileName;
                this.TestNames.Add(className);
                testNumber++;
            }

            FileHelpers.AddFilesToZipArchive(submissionZipFilePath, string.Empty, filePaths);
            FileHelpers.DeleteFiles(filePaths);
        }

        protected virtual void AddTestRunnerTemplate(string submissionFilePath)
        {
            // It is important to call the JUintTestRunnerCodeTemplate after the TestClasses have been filled
            // otherwise no tests will be queued in the JUnitTestRunner, which would result in no tests failing.
            File.WriteAllText(this.JUnitTestRunnerSourceFilePath, this.JUnitTestRunnerCode);
            FileHelpers.AddFilesToZipArchive(submissionFilePath, string.Empty, this.JUnitTestRunnerSourceFilePath);
            FileHelpers.DeleteFiles(this.JUnitTestRunnerSourceFilePath);
        }

        protected virtual void ExtractUserClassNames(string submissionFilePath)
        {
            this.UserClassNames.AddRange(
                FileHelpers.GetFilePathsFromZip(submissionFilePath)
                    .Where(x => !x.EndsWith("/") && x.EndsWith(Constants.JavaSourceFileExtension))
                    .Select(x => x.Contains(".") ? x.Substring(0, x.LastIndexOf(".", StringComparison.Ordinal)) : x)
                    .Select(x => x.Replace("/", ".")));
        }

        private Dictionary<string, string> GetTestErrors(string receivedOutput)
        {
            var errorsByFiles = new Dictionary<string, string>();
            var output = new StringReader(receivedOutput);
            var line = output.ReadLine();
            while (line != null)
            {
                var firstSpaceIndex = line.IndexOf(" ", StringComparison.Ordinal);
                var fileName = line.Substring(0, firstSpaceIndex);
                var errorMessage = line.Substring(firstSpaceIndex);
                errorsByFiles.Add(fileName, errorMessage);
                line = output.ReadLine();
            }

            return errorsByFiles;
        }
    }
}
