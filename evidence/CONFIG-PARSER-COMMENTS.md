# Config-parser PR comments

The no-key variant below is the body posted to public PR 1. The key-enabled variant uses the same fresh findings and a fake Anthropic transport with a grounded response; no `ANTHROPIC_API_KEY` was available for a live model call.

## Without `ANTHROPIC_API_KEY`

## BehaviorDiff runtime analysis

### Edited-code coverage
**1 of 1 edited files were exercised by tests.**
1 member, 5 call sites, and 10 total calls were observed in representative base/PR runs.

**UNEXPECTED: 1 member(s), across 2 call site(s).**

Unexpected means runtime behavior changed in a file the PR did not modify. That is the point of this analysis.

### Unexpected members

<details>
<summary><code>SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)</code> - 2 tests, 2 call sites</summary>

**Observed values**
- `SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping`: `IsFreeShipping(orderTotal=Primitive:40)` returned `Primitive:false`; PR returns `Primitive:true`.
- `SampleApp.Tests.ShippingTests.Totals_are_never_negative`: `IsFreeShipping(orderTotal=Primitive:45)` returned `Primitive:false`; PR returns `Primitive:true`.

**Tests and assertions**
- **2 tests executed this; 1 test had an assertion react.**
- `SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping`: an assertion reacted.
- `SampleApp.Tests.ShippingTests.Totals_are_never_negative`: no assertion reacted.

**Call paths**
- `SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping` (base and PR): `SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping()` -> `SampleApp.ShippingCalculator.TotalWithShipping(System.Decimal)` -> `SampleApp.ShippingCalculator.ShippingCost(System.Decimal)` -> `SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)`
- `SampleApp.Tests.ShippingTests.Totals_are_never_negative` (base and PR): `SampleApp.Tests.ShippingTests.Totals_are_never_negative()` -> `SampleApp.ShippingCalculator.TotalWithShipping(System.Decimal)` -> `SampleApp.ShippingCalculator.ShippingCost(System.Decimal)` -> `SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)`

**Source**
- `samples/SampleApp/ShippingCalculator.cs:10`

**Edited-file reachability**
- No edited file appears on these recorded test-to-member paths.

</details>

**EXPECTED: 0 member(s), across 0 call site(s).**

GitHub cannot anchor review comments on outside-diff files, which is why this analysis exists.

<!-- behaviordiff:github:pr:1:summary -->

## With `ANTHROPIC_API_KEY` (fake grounded response)

## BehaviorDiff runtime analysis

### Edited-code coverage
**1 of 1 edited files were exercised by tests.**
1 member, 5 call sites, and 10 total calls were observed in representative base/PR runs.

**UNEXPECTED: 1 member(s), across 2 call site(s).**

Unexpected means runtime behavior changed in a file the PR did not modify. That is the point of this analysis.

### Unexpected members

<details>
<summary><code>SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)</code> - 2 tests, 2 call sites</summary>

**Observed values**
- `SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping`: `IsFreeShipping(orderTotal=Primitive:40)` returned `Primitive:false`; PR returns `Primitive:true`.
- `SampleApp.Tests.ShippingTests.Totals_are_never_negative`: `IsFreeShipping(orderTotal=Primitive:45)` returned `Primitive:false`; PR returns `Primitive:true`.

**Tests and assertions**
- **2 tests executed this; 1 test had an assertion react.**
- `SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping`: an assertion reacted.
- `SampleApp.Tests.ShippingTests.Totals_are_never_negative`: no assertion reacted.

**Call paths**
- `SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping` (base and PR): `SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping()` -> `SampleApp.ShippingCalculator.TotalWithShipping(System.Decimal)` -> `SampleApp.ShippingCalculator.ShippingCost(System.Decimal)` -> `SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)`
- `SampleApp.Tests.ShippingTests.Totals_are_never_negative` (base and PR): `SampleApp.Tests.ShippingTests.Totals_are_never_negative()` -> `SampleApp.ShippingCalculator.TotalWithShipping(System.Decimal)` -> `SampleApp.ShippingCalculator.ShippingCost(System.Decimal)` -> `SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)`

**Source**
- `samples/SampleApp/ShippingCalculator.cs:10`

**Edited-file reachability**
- No edited file appears on these recorded test-to-member paths.

**Optional model explanation** (`claude-sonnet-5`, accepted only after literal and exact-citation grounding checks)
- Why: The PR changes DefaultFreeShippingThreshold, and IsFreeShipping now returns true instead of false for the observed input 40.
- Suggested test: Add a test that applies the default settings, calls IsFreeShipping with 40, and expects false; it passes on base and fails on the PR because the result is true.

</details>

**EXPECTED: 0 member(s), across 0 call site(s).**

GitHub cannot anchor review comments on outside-diff files, which is why this analysis exists.

<!-- behaviordiff:github:pr:1:summary -->
