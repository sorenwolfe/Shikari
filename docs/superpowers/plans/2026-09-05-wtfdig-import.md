# WTFDIG import

Approved scope: a fight-link importer with strategy, role/group and explicit variant selection, guide preview and source attribution; editable geometry through linked raidplan.io boards. Imported notes/timestamps do not enable inferred mechanics. Source image references are preserved without claiming they are editable positions.

Implementation: fixed-host HTTPS source fetch for the public mczub/wtfdig repository's route data.ts. A bounded non-executing literal reader handles data objects, arrays, local constant references and string concatenation. Calls, template interpolation and unknown expressions become explicit unsupported markers. Never execute downloaded TypeScript. Resolve the selected strategy and variants conservatively, keeping a conversion report. Network and parsing use background Tasks; the UI consumes completed immutable results and commits on its own thread only after a preview. Cancellation/unload must not mutate plugin state from workers.

1. Tests for URL validation, literal parsing/references, unsupported expressions, role/variant mapping and disabled imported timings.
2. Implement bounded source reader, guide model, mapper and capped/cancellable fetcher.
3. Add WTFDIG panel, strategy/role/variant preview and selectable linked editable board imports.
4. Add source hash/retrieval metadata and notes identifying missing images/diagram conversion.
5. Run fake-network, mapper and regression tests; independent review; package a checked incremental patch against D:/Shikari. Live network/SDK checks are separately reported if sandbox access prevents them.
