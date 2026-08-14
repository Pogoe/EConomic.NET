using Xunit;

// These tests share one agreement and write to it: they create customers, products and invoices,
// and several assert on how many of something the agreement now holds. Running the classes in
// parallel would interleave those creations and deletions, so a count taken by one test would
// include another's fixtures. e-conomic also reuses identifiers, which makes concurrent
// create-and-delete cycles genuinely ambiguous rather than merely noisy.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
