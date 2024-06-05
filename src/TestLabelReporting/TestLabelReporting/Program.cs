using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using NUnit.Framework;

namespace TestLabelReporting
{
    internal abstract class Program
    {
        private class FixtureData
        {
            public string TestFixture { get; set; }
            public string Application { get; set; }
            public string DomainOwner { get; set; }
            public string TestType { get; set; }
        }
        
        private class TestData
        {
            public string TestName { get; set; }
            public string TestFixture { get; set; }
            public string Environments { get; set; }
            public string FeatureTypes { get; set; }
            public string Categories { get; set; }
        }

        private static MemoryStream WriteFixtureDataToStream(List<FixtureData> fixtureObjects, string repositoryName) 
        {
            var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream)) 
            {
                writer.WriteLine("test_fixture,application,domain_owner,test_type,repository_name");
                foreach (var fixtureObject in fixtureObjects)
                {
                    writer.WriteLine($"{fixtureObject.TestFixture},{fixtureObject.Application},{fixtureObject.DomainOwner},{fixtureObject.TestType},{repositoryName}");
                }
                
                writer.Flush();
                stream.Seek(0, SeekOrigin.Begin);
                return new MemoryStream(stream.ToArray(), false);
            }
        }
        
        private static MemoryStream WriteTestDataToStream(List<TestData> testObjects, string repositoryName) 
        {
            var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream)) 
            {
                writer.WriteLine("test_name,test_fixture,environments,feature_types,categories,repository_name");
                foreach (var testObject in testObjects)
                {
                    writer.WriteLine($"{testObject.TestName},{testObject.TestFixture},{testObject.Environments},{testObject.FeatureTypes},{testObject.Categories},{repositoryName}");
                }
                
                writer.Flush();
                stream.Seek(0, SeekOrigin.Begin);
                return new MemoryStream(stream.ToArray(), false);
            }
        }
        
        private static bool StoreDataInBucket(MemoryStream stream, string bucket, string key)
        {
            try
            {
                var s3Client = new AmazonS3Client(RegionEndpoint.USEast1);
                var transferUtility = new TransferUtility(s3Client);
                transferUtility.Upload(stream, bucket, key);

                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("An error occurred uploading the file to S3:");
                Console.Error.WriteLine(ex.Message);

                return false;
            }
        }

        private static IEnumerable<FixtureData> PullFixtureData(Assembly assembly)
        {
            var fixtureDataList = new List<FixtureData>();
            var testFixtures = assembly.GetTypes().Where(
                t => t.Name.EndsWith("Fixture")
            ).ToList();
                
            foreach (var testFixture in testFixtures)
            {
                fixtureDataList.Add(new FixtureData
                {
                    TestFixture = testFixture.Name,
                    Application = testFixture.GetCustomAttributesData()
                        .FirstOrDefault(a => a.AttributeType.Name == "TestApplicationAttribute")
                        ?.ConstructorArguments.FirstOrDefault()
                        .Value.ToString(),
                    DomainOwner = testFixture.GetCustomAttributesData()
                        .FirstOrDefault(a => a.AttributeType.Name == "TestDomainOwnerAttribute")
                        ?.ConstructorArguments.FirstOrDefault()
                        .Value.ToString(),
                    TestType = testFixture.GetCustomAttributesData()
                        .FirstOrDefault(a => a.AttributeType.Name == "TestTypeAttribute")
                        ?.ConstructorArguments.FirstOrDefault()
                        .Value.ToString()
                });
            }

            return fixtureDataList;
        }
        
        private static IEnumerable<TestData> PullTestData(Assembly assembly)
        {
            var testDataList = new List<TestData>();
            var tests = assembly.GetTypes()
                .SelectMany(t => t.GetMethods())
                .Where(t => 
                t.GetCustomAttributes(typeof(TestAttribute), false).Length > 0).ToList();
                
            foreach (var test in tests)
            {
                var environments = string.Join(",", test.GetCustomAttributesData()
                    .Where(a => a.AttributeType.Name == "TestEnvironmentAttribute")
                    .Select(x => x.ConstructorArguments.FirstOrDefault().ToString().Trim('"'))
                    .Distinct());
                var featureTypes = string.Join(",", test.GetCustomAttributesData()
                    .Where(a => a.AttributeType.Name == "TestFeatureTypeAttribute")
                    .Select(x => x.ConstructorArguments.FirstOrDefault().ToString().Trim('"'))
                    .Distinct());
                var categories = string.Join(",", test.GetCustomAttributesData()
                    .Where(a => a.AttributeType.Name == "CategoryAttribute")
                    .Select(x => x.ConstructorArguments.FirstOrDefault().ToString().Trim('"'))
                    .Distinct());
                
                testDataList.Add(new TestData
                {
                    TestName = test.Name,
                    TestFixture = test.ReflectedType?.Name,
                    Environments = $"\"{environments}\"",
                    FeatureTypes = $"\"{featureTypes}\"",
                    Categories = $"\"{categories}\"",
                });
            }

            return testDataList;
        }

        private static bool UploadExportFileToS3(MemoryStream stream, string repositoryName, string dataType)
        {
            var currentTimestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
            var filename = $"{repositoryName}__{dataType}__{currentTimestamp}.csv";
            
            return StoreDataInBucket(stream, "psi-reporting-data", $"external_data/automated_test_data/{filename}");
        }
        
        public static int Main(string[] args)
        {
            // TODO: Parse args better
            // TODO: Named args?
            if (args == null || args.Length == 0)
            {
                Console.WriteLine("Error: input RepositoryName is null");
                return -1;
            }

            var repositoryName = args[0];
            
            var assemblyInput = args[1];
            var assemblyPaths = assemblyInput.Split(',');
            
            // var assemblies = new List<string> {"PatriotSoftware.Care.SmokeTests", "PatriotSoftware.Suite.SmokeTests", "PatriotSoftware.MyPatriot.SmokeTests"};
            var fixtureDataList = new List<FixtureData>();
            var testDataList = new List<TestData>();
            
            foreach (var assemblyPath in assemblyPaths)
            {
                var assembly = Assembly.LoadFrom(assemblyPath);
                fixtureDataList.AddRange(PullFixtureData(assembly));
                testDataList.AddRange(PullTestData(assembly));
            }
            
            var fixtureDataStream = WriteFixtureDataToStream(fixtureDataList, repositoryName);
            var uploadedFixtureDataSuccessfully = UploadExportFileToS3(fixtureDataStream, repositoryName, "test_fixtures");
            
            var testDataStream = WriteTestDataToStream(testDataList, repositoryName);
            var uploadedTestDataSuccessfully = UploadExportFileToS3(testDataStream, repositoryName, "automated_tests");

            var success = uploadedFixtureDataSuccessfully && uploadedTestDataSuccessfully;
            
            if (!success)
            {
                return -1;
            }

            return 0;
        }
    }
}