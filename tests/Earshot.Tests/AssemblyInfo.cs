using Microsoft.VisualStudio.TestTools.UnitTesting;

// Some tests set process environment variables (EARSHOT_DATA_ROOT, EARSHOT_SAFE_MODE), which are
// shared by every test in the process, so tests run one at a time.
[assembly: DoNotParallelize]
