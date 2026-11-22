# Licensing decision and scope

Updated November 21, 2026.

The owner, Shayne Winter, approved **Pegasus Community License 1.0** for Pegasus
Cortex and confirmed ownership of its bundled first-party Pegasus.Shared package
and SharedMcp source snapshot. The operative grant is [LICENSE](../LICENSE).
This document explains that decision; it does not replace the license.

Personal use is free. Organizations below US$10 million in combined trailing-
twelve-month group revenue may use the product commercially under the community
terms. Organizations at or above that threshold require a separate signed paid
agreement. An employee, contractor or small subsidiary cannot use individual or
small-business eligibility on behalf of an ineligible group.

The grant permits source and precompiled installers, modifications, plugins,
integrations and useful derivatives. It requires accurate attribution, continued
license/notices and independent eligibility for recipients. No general noncompete,
runtime revenue checking, activation service or separate enterprise code fork was
introduced. Commercial permission does not prevent continued free community
development or distribution.

Bundled third-party software, Windows runtime components, tools and artwork
retain their own terms. Their notices and provenance are in [licenses](../licenses/).
User data and independent outputs do not automatically inherit the product
license. The first-party package and source snapshot remain pinned; an external
owner grant does not alter their original package metadata or bytes.

## Choice of terms

The earlier research considered PolyForm Small Business, LTX/LFM model community
licenses and Business Source License. None was adopted verbatim. The stock
software thresholds differed, model licenses contained vendor/model-specific
scope, and automatic later conversion did not match the selected policy.

The resulting terms use their own Pegasus name and explicitly cover software.
Business-size restrictions mean the product is described as **source-available
community software**, rather than OSI-compliant open source. This is a recorded
owner decision and wording review, not an attorney's opinion or an enforceability
guarantee.

Primary research references:
- [Software-license foundation](https://polyformproject.org/licenses/small-business/1.0.0)
- [Foundation text reuse](https://raw.githubusercontent.com/polyformproject/polyform-licenses/1.0.0/LICENSE.md)
- [LTX model-license comparison](https://raw.githubusercontent.com/Lightricks/LTX-2/main/LICENSE-2_x)
- [Open Source Definition](https://opensource.org/osd)

## Release privacy

The public source starts with a clean snapshot. Earlier private commit history
and the 19.02 private release are preserved in a separate private repository.
Public distribution includes public package-verification certificates, never
their private keys. Machine-local MCP certificates and user state are configured
on the target computer rather than copied from the development machine.
