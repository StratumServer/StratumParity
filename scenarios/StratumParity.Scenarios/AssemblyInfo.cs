using Xunit;

// Required by Atlas: one embedded server per test class, classes must run sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// The probe blocks mod is staged per class ([AtlasWorld(Mods = ...)] on the random tick and
// block tick contract classes) rather than assembly-wide: the extra mod lengthens the
// off-thread server assets packet build at every boot, which proved crash-prone on
// slow CI runners for classes that never use the block.
