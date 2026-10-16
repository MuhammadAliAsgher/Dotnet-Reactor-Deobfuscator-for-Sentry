using Sentry_Crashes_Deobfuscator.Repositories;
using Xunit;

namespace Deobfuscator.Tests
{
    public class LocalDiskNrmapBlobStoreTests
    {
        [Fact]
        public void TryResolve_FileExists_ReturnsPath()
        {
            var root = Path.Combine(Path.GetTempPath(), "nrmap-blob-test-" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(root, "demo-app"));
            var expectedPath = Path.Combine(root, "demo-app", "1.4.2.nrmap");
            File.WriteAllText(expectedPath, "irrelevant content");

            try
            {
                var store = new LocalDiskNrmapBlobStore(root);

                var resolved = store.TryResolve("demo-app", "1.4.2");

                Assert.Equal(expectedPath, resolved);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void TryResolve_FileMissing_ReturnsNull()
        {
            var root = Path.Combine(Path.GetTempPath(), "nrmap-blob-test-" + Guid.NewGuid());
            Directory.CreateDirectory(root);

            try
            {
                var store = new LocalDiskNrmapBlobStore(root);

                var resolved = store.TryResolve("demo-app", "1.4.2");

                Assert.Null(resolved);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
