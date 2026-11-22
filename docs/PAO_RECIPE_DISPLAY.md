# Passive recipe display metadata, version 1

Recipe descriptions are optional author declarations. They never grant execution,
change a recipe, replace its pins, or establish registration or runtime acceptance.
Recipe execution and the `recipes.v1.json` schema are unchanged.

The read-only consumer looks for the fixed sibling `recipe-display.v1.json` beside
the existing recipe registry. No automatic import, discovery, download, publication,
or installed metadata write is performed. Missing, unreadable, invalid or unmatched
metadata affects display only. The viewer retains an explicit UNKNOWN fallback.

## Exact binding and generation

Every record binds this complete key, using ordinal exact comparisons:

- `recipeName`: the normalized registry name.
- `definitionVersion`: the exact positive integer.
- `scriptSha256`: the exact lowercase SHA256, or explicit null for a definition
  without a script pin. Missing is not a wildcard.
- `generation`: the lowercase SHA256 defined below. It is not a release number,
  author batch label, profile generation, or a name-only match.

The generation contract is `pegasus.agentops.recipe-definition-generation/1`.
Compute it from the **full normalized existing recipe DTO**:

1. Serialize the DTO as camelCase JSON, including nulls and default properties.
   Enums are their existing integer values. Normalization belongs to the existing
   recipe registry; do not hash a sparse registration request instead.
2. Recursively sort object properties with ordinal UTF-16 key ordering. Preserve
   array order. Integer numbers use invariant base-10 without leading zeros.
3. Encode unindented JSON as UTF-8 without BOM. String escapes are `\"`, `\\`,
   `\b`, `\f`, `\n`, `\r`, `\t`; other characters below U+0020 use lowercase
   `\u00xx`. Other Unicode scalar values, including U+2028, remain literal UTF-8.
   Slashes and HTML characters are not additionally escaped.
4. Hash the UTF-8 domain string
   `pegasus.agentops.recipe-definition-generation/1` followed by one LF byte,
   followed immediately by those canonical JSON bytes, with no trailing newline.
5. Return 64 lowercase hexadecimal characters.

`RecipeDisplayMetadata.Generation` is the provider implementation. The safe compact
list exposes this digest; it never exposes the underlying argument/default values.
An author publisher can use the digest returned for the exact selected registry
identity rather than retrieving or forwarding full definitions. A definition change
invalidates its old descriptor even when name, version and script pin stay equal.

The focused fixture has an independently constructed canonical Unicode/array vector:
`dc455c33013067082ac983d2d238d99e1297372bbf2120c3f36b4b34af9c178c`.
Its inputs are synthetic, not a registered recipe generation.

## Catalog contract

The top-level object has exactly `schema` and `records`:

```json
{
  "schema": "pegasus.agentops.recipe-display/1",
  "records": [
    {
      "key": {
        "recipeName": "example-recipe",
        "definitionVersion": 3,
        "scriptSha256": "<exact lowercase SHA256 or null>",
        "generation": "<exact normalized-definition generation SHA256>"
      },
      "title": "Prepare an input",
      "summary": "An author-reviewed description of the selected recipe.",
      "steps": ["First declared step.", "Second declared step."],
      "tools": ["Declared tool or downstream component"],
      "inputs": ["Meaningful input description; no value or example"],
      "outputs": ["Declared result; no execution claim"],
      "conditions": ["Declared prerequisite or limitation"],
      "authorState": "held",
      "provenance": {
        "sourceId": "author-contract.v1",
        "sourceArtifactSha256": "<SHA256 of the immutable author source>",
        "sourceGroup": "exact-source-generation-group",
        "sourceScope": "What the author actually inspected or declared.",
        "isFixture": false
      }
    }
  ]
}
```

This is an illustrative shape, not a valid catalog with usable hashes. Unknown
members, duplicate JSON members, duplicate full keys and unsupported schemas are
rejected. The catalog is bounded to 1 MiB and at most 256 records. Text limits are
UTF-8 bytes: title 128, summary 512, source ID/group 128 each, source scope 512.
Each of steps/tools/inputs/outputs/conditions has at most 12 single-line entries,
each at most 200 bytes. Control characters and blank text entries are rejected.

Allowed author states are `declared`, `held` and `prepared`. These are authoring
claims, not execution readiness. Fixture-bound records must set `isFixture=true`;
the viewer labels them accordingly. Authors must review and redact their text:
no credentials, raw arguments, defaults/examples, environment values, literal
paths, script bodies or unverified execution claims. This parser is not a general
secret detector. Passive display text never becomes an execution instruction.

## Compact list and selected detail

The existing internal typed `recipe` administration transport is reused:

- `op=ls` retains the original five fields and their enum representations, adding
  only `generation` and `hasDisplay`. It contains no summary, steps or expanded
  descriptor arrays, regardless of catalog size.
- `op=get` requires the exact `name` and expected `generation` from that list.
  It reads the current definition and returns `precondition` on a stale generation.
  It returns one safe bounded recipe-detail row, optional matched `display`, and
  `displayStatus`: `matched`, `missing`, `invalid` or `identity_mismatch`.

This selected read is classified read-only. It adds no public MCP tool or CLI
operation. The Tray fetches only the selected record, caches one exact identity,
cancels superseded reads and discards late replies after selection/refresh changes.
The UI validates all four descriptor coordinates again before using author text.
Technical details remain secondary and contain no raw values.

Actual installed metadata publication is a separate authorized action. A source
fixture or prepared descriptor is not proof that an installed catalog exists or
that a currently registered generation matches it.
