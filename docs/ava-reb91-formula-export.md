# AVA formula export: REB 23.003 (2009)

## Wire contract

`TakeoffLine` and `QuantityTakeoffRow` append optional `FormulaExpression` and `CalculationApproach` fields. Existing positional arguments retain their order. A formula row must provide both fields. The symbolic expression is the exact stored template formula; the numeric approach has substituted values and explicit physical-unit conversion factors, **without** the final `=`. Direct quantities/constants retain their existing three-decimal commercial-rounding behavior.

The takeoff command copies both fields to the export rows. Formula arithmetic uses the existing dimension-aware evaluation rules, then converts into the **existing LV unit**. Neither the source `Item/Qty`, LV unit, nor catalog is rewritten. For example, `sqrt(Volumen/Schnittlänge)*3` has length dimension and must still be rejected for an area LV unit. `sqrt(Volumen/Schnittlänge)*Schnittlänge` has area dimension.

## REB91 syntax and physical rows

The primary specification is the federal **REB-VB 23.003, Ausgabe 2009**, section 2.1.4 (free mathematical notation), and section 2.2.2.2 (DA11 fields), distributed by Baden-Württemberg's Ministry of Transport:

`https://vm.baden-wuerttemberg.de/fileadmin/redaktion/m-mvi/intern/Dateien/MobiZ/AZ_62-0278_ADIS-FAK32-1f.zip`

The ZIP contains `REB-VB 23.003.pdf`.

- Power/root operator: `**`. `sqrt(x)` is emitted as `(x**(0,5))`; `^` and `sqrt` are not exported.
- REB forbids exponent one. The renderer emits its equivalent basis instead, retaining the original symbolic formula separately.
- Signed operands are parenthesized so two arithmetic operators never immediately follow each other.
- Before DA31 serialization, every numeric literal is rounded commercially to at most three decimal places. The emitted arithmetic is evaluated again and compared with the quantity rounded to three places in the unchanged LV unit; inconsistent approaches fail closed.
- Raw rows remain 80 characters: remark columns 14–22 (9), method columns 30–31 (`91`), approach columns 32–69 (38), address 70–75.
- A logical approach can use at most 20 calculation rows. Every continuation repeats `91`; the final row alone ends with `=`. A number or `**` token is never split. Oversized/unrepresentable expressions fail closed, never become precomputed fallback constants.

`X31Writer.EvaluateReb91` accepts the **numeric subset emitted here** (optionally with final `=`). It does not implement address references or REB trigonometric functions; it is not a general third-party REB interpreter. Formula-parser limits also apply (256 nodes, nesting depth 32, expression length 2048); additional conversion parentheses may reach those bounds before the original symbolic expression does. Rejection is intentional rather than emitting an unchecked calculation.

## Remarks and identity comments

Use `(ID: xxx)`, not `Revit-ElementID`. When the ID and formula fit together in the nine-character remark, both are written there. Otherwise the complete ID is placed in a preceding `*` comment. A formula that does not fit is split between preceding plain `*` comment rows (maximum 56 content characters each) and the calculation remark; no `Formel:` label is added. The writer preserves formula text across field boundaries and does not truncate IDs or expressions. Group comments precede the ID/formula rows for each LV item so group context is unambiguous in standard DA31 rows.

The X31 writer emits only GAEB DA31 namespace elements and fixed-field REB rows; it does not add BVBS vendor-extension elements or attributes. Group headings are repeated where needed per LV item, while the review reader consolidates equal headings and their ElementIDs. A group heading that would exceed the 56-character REB comment field is rejected rather than truncated. The review displays evaluated results rounded to at most three decimal places.

Example: volume `1.30086 m3`, cut length `14.454 m`, stored formula `sqrt(Volumen/Schnittlänge)*Schnittlänge`, existing LV unit `m2`:

```
sqrt(Volumen/Schnittlänge)*Schnittlänge
REB91:   (((1,301/14,454)**(0,5))*14,454)=
Ergebnis: 4,336 m2
```

## X82 precision boundary

X82 still publishes the unchanged existing cumulative, three-decimal `QtyDeterm/Qty` total. Formula rows retain real arithmetic. If cumulative allocation would require modifying a formula result (for example two formula rows of `0.0005`, whose individually rounded sum differs from the rounded total), X82 export aborts with a precision diagnostic. It does not alter the LV total, substitute constants, or manufacture correction terms. Existing non-formula allocation behavior is unchanged.

## Verification scope

Tests exercise real takeoff/formula/writers, saved-byte readback, direct official DA31/DA82 schema validation, 20-line continuations, long IDs/formulas, three-decimal calculation/result handling, unit conversion, and the command using Revit/UI doubles. These are not live-Revit or third-party importer acceptance results.
