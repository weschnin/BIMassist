using BIMassist.Ava.Core;

internal static class Program
{
    private static int passed, failed;
    private static void Main()
    {
        BgkReferenceTransferTests.Run();
        Test("constant positions use no quantity parameter", () =>
        {
            var template = Template();
            var calls = new List<(ParameterSource Source, string Unit)>();
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (s, u) =>
            {
                calls.Add((s, u));
                throw new Exception("Constant tried to read quantity source.");
            })]);
            Equal(1, result.Examined);
            Equal(0, result.Unmarked);
            Equal(0, result.Unmapped);
            Equal(0, result.Errors.Count);
            var row = result.Rows.Single();
            Equal("lv1", row.LvItemId);
            Equal(2m, row.Value);
            Equal("St", row.Unit);
            Equal(0, calls.Count);
            Contains("e1", row.Explanation);
            Contains(template.Mappings[0].Id.ToString(), row.Explanation);
            Contains(template.Mappings[0].Positions[0].Id.ToString(), row.Explanation);
            Contains("2", row.Explanation);
            Contains("St", row.Explanation);
        });
        Test("zero rows explain unmarked, unmapped and failed conditions per element", () =>
        {
            var template = Template();
            template.Mappings[0].Conditions = [new() { Source = Local("DN"), Operand = OperandKind.Numeric,
                Operator = ConditionOperator.Equal, Number = 150m, Unit = "mm" }];
            var result = QuantityTakeoff.Calculate(template, [
                Element("unmarked", template, (_, _) => new(Number: 125m), bgk: "", type: "02"),
                Element("unmapped", template, (_, _) => new(Number: 125m), bgk: "001", type: "77"),
                Element("condition", template, (_, _) => new(Number: 125m))]);
            Equal(0, result.Rows.Count); Equal(0, result.Errors.Count);
            Equal(3, result.Skipped.Count);
            Contains("unmarked", result.Skipped[0].Message);
            Contains("BGK", result.Skipped[0].Message);
            Contains("77", result.Skipped[1].Message);
            Contains("DN", result.Skipped[2].Message);
            Contains("125", result.Skipped[2].Message);
            Contains("150", result.Skipped[2].Message);
        });
        Test("zero-row element details are bounded even when Revit text is oversized", () =>
        {
            var template = Template();
            var oversized = new string('X', 10000);
            var result = QuantityTakeoff.Calculate(template, [Element("long-id", template, (_, _) => new(Number: 0), bgk: oversized)]);
            Equal(1, result.Skipped.Count);
            if (result.Skipped[0].Message.Length > 600) throw new Exception("Skip diagnostic exceeds its UI bound");
            Contains("…", result.Skipped[0].Message);
        });
        Test("physical quantity values convert to the actual LV unit and incompatible units block export", () =>
        {
            var template = Template();
            var position = template.Mappings[0].Positions[0];
            position.Quantity = new() { Kind = QuantityKind.Parameter, Source = Local("Length"), Unit = "cm" };
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = "m" };
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, unit) => {
                Equal("cm", unit); return new(Number: 125m);
            })]);
            Equal(0, result.Errors.Count);
            Equal(1.25m, result.Rows.Single().Value);
            Equal("m", result.Rows.Single().Unit);
            Contains("125 cm", result.Rows.Single().Explanation);
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = "kg" };
            result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => new(Number: 125m))]);
            Equal(0, result.Rows.Count); Equal(1, result.Errors.Count);
            Contains("nicht umrechenbar", result.Errors.Single().Message);
        });
        Test("unit conversion covers linear, square, cubic, angular and alias units without relabeling", () =>
        {
            Equal(1.25m, UnitCatalog.Convert(125m, "cm", "m"));
            Equal(1m, UnitCatalog.Convert(1000000m, "mm2", "m²"));
            Equal(1m, UnitCatalog.Convert(1000000m, "cm3", "m³"));
            Equal(1000m, UnitCatalog.Convert(1m, "km", "m"));
            Equal(180m, decimal.Round(UnitCatalog.Convert(3.1415926535897932384626433833m, "rad", "deg"), 8));
            Reject<InvalidDataException>(() => UnitCatalog.Convert(1m, "m", "St"));
            Reject<InvalidDataException>(() => UnitCatalog.Convert(decimal.MaxValue, "km", "mm"));
        });
        Test("a NUMBER/INTEGER parameter may explicitly carry the exact LV unit as an unconverted direct quantity", () =>
        {
            foreach (var spec in new[] { "NUMBER", "INTEGER" })
            {
                var template = Template();
                template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = "h" };
                template.Mappings[0].Positions[0].Quantity = new() { Source = Local("AVA_Anzahl", spec), Unit = "h", AllowCustomUnit = true };
                var expected = spec == "INTEGER" ? 7m : 7.5m;
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, unit) =>
                {
                    Equal("1", unit); return new(Number: expected);
                })]);
                Equal(0, result.Errors.Count); Equal(expected, result.Rows.Single().Value);
                Equal("h", result.Rows.Single().Unit);
                Contains("Rohwert ohne Umrechnung", result.Rows.Single().Explanation);
                var diagnostics = TemplateValidator.Validate(template);
                if (diagnostics.Any(d => d.Code == "QUANTITY_UNIT_UNVERIFIABLE"))
                    throw new Exception("Explicit direct scalar unit was incorrectly reported as an export-blocking unverifiable unit");

                template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = "min" };
                result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => new(Number: expected))]);
                Equal(0, result.Rows.Count); Equal(1, result.Errors.Count);
                Contains("Manuell deklarierte Einheit", result.Errors.Single().Message);
                Contains("bleibt unverändert", result.Errors.Single().Message);
            }
        });
        Test("NUMBER/INTEGER retain established direct St and psch quantities", () =>
        {
            foreach (var spec in new[] { "NUMBER", "INTEGER" })
            foreach (var unit in new[] { "St", "psch" })
            {
                var template = Template();
                template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = unit };
                template.Mappings[0].Positions[0].Quantity = new() { Source = Local("AVA_Anzahl", spec), Unit = unit };
                var expected = spec == "INTEGER" ? 7m : 7.5m;
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, requestedUnit) =>
                {
                    Equal(unit, requestedUnit); return new(Number: expected);
                })]);
                Equal(0, result.Errors.Count); Equal(expected, result.Rows.Single().Value); Equal(unit, result.Rows.Single().Unit);
            }
        });
        Test("invalid templates are rejected before enumerating elements", () =>
        {
            var template = Template();
            template.Mappings[0].Positions[0].Quantity!.ConstantValue = -1;
            Reject<InvalidDataException>(() => QuantityTakeoff.Calculate(template, ForbiddenElements()));
            template = Template(); template.Mappings.Clear();
            Reject<InvalidDataException>(() => QuantityTakeoff.Calculate(template, ForbiddenElements()));
        });
        Test("parameter siblings retain their declared unit and decimal value", () =>
        {
            var template = Template();
            template.LvCatalog.Entries.Add(new("lv2", "boq", ["02"], "02", "Length", "", "cm"));
            var source = Local("Length");
            template.Mappings[0].Positions.Add(new() { LvItemId = "lv2", Quantity = new() { Source = source, Unit = "cm" } });
            var calls = new List<string>();
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (s, u) =>
            {
                Equal(source, s); calls.Add(u); return new(Number: 125.125m);
            })]);
            Equal(0, result.Errors.Count);
            Equal(2, result.Rows.Count);
            Equal(125.125m, result.Rows[1].Value);
            Equal("cm", result.Rows[1].Unit);
            Equal("cm", calls.Single());
            Contains("125.125", result.Rows[1].Explanation);
            Contains("Length", result.Rows[1].Explanation);
        });
        Test("formula siblings bind numeric values in their own declared input units", () =>
        {
            var template = Template();
            template.LvCatalog.Entries.Add(new("lv2", "boq", ["02"], "02", "Length", "", "cm"));
            template.LvCatalog.Entries.Add(new("lv3", "boq", ["03"], "03", "Formula", "", "m2"));
            template.Mappings[0].Positions.Add(new() { LvItemId = "lv2", Quantity = new() { Source = Local("Length"), Unit = "cm" } });
            template.Mappings[0].Positions.Add(new() { LvItemId = "lv3", Quantity = new()
            {
                Kind = QuantityKind.Formula, Source = null, Expression = "[Length] * Width / 2", Unit = "m2",
                Variables = [new() { Name = "Length", Source = Local("Length"), Unit = "m" },
                    new() { Name = "Width", Source = Local("Width"), Unit = "m" }]
            } });
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (s, u) =>
                new(Number: (s.Name, u) switch { ("Length", "cm") => 400, ("Length", "m") => 4,
                    ("Width", "m") => 3, _ => throw new Exception("Wrong binding or unit") }))]);
            Equal(0, result.Errors.Count);
            Equal(3, result.Rows.Count);
            Equal(400m, result.Rows[1].Value);
            Equal(6m, result.Rows[2].Value);
            Equal("m2", result.Rows[2].Unit);
            Contains("[Length] * Width / 2", result.Rows[2].Explanation);
            Contains("Length=4 m", result.Rows[2].Explanation);
            Contains("Width=3 m", result.Rows[2].Explanation);
        });
        Test("beam formulas retain instance/type identities, physical area and immutable LV units", () =>
        {
            var template = Template();
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = "m²" };
            var b = Local("B", scope: ParameterScope.Type);
            var h = Local("H", scope: ParameterScope.Type);
            var cut = Local("Schnittlänge");
            var volume = Local("Volumen", "VOLUME");
            template.Mappings[0].Positions = [
                Position(new() { Kind = QuantityKind.Formula, Source = null, Unit = "m²",
                    Expression = "(2*H+B)*Schnittlänge", Variables = [
                        new() { Name = "H", Source = h, Unit = "m" }, new() { Name = "B", Source = b, Unit = "m" },
                        new() { Name = "Schnittlänge", Source = cut, Unit = "m" }] }),
                Position(new() { Kind = QuantityKind.Formula, Source = null, Unit = "cm2",
                    Expression = "sqrt(Volumen/Schnittlänge)*Schnittlänge", Variables = [
                        new() { Name = "Volumen", Source = volume, Unit = "m3" },
                        new() { Name = "Schnittlänge", Source = cut, Unit = "m" }] }) ];
            var reads = new List<(ParameterSource Source, string Unit)>();
            var result = QuantityTakeoff.Calculate(template, [new TakeoffElement("beam-42", (source, unit) =>
            {
                if (ReferenceEquals(source, template.BgkIdentification)) return new(Text: "001");
                if (ReferenceEquals(source, template.TypeIdentification)) return new(Text: "02");
                reads.Add((source, unit));
                return source switch
                {
                    { Name: "B" or "H", Scope: ParameterScope.Type, Spec: "LENGTH" } when unit == "m" => new(Number: 0.3000m),
                    { Name: "Schnittlänge", Scope: ParameterScope.Instance, Spec: "LENGTH" } when unit == "m" => new(Number: 14.4540m),
                    { Name: "Volumen", Scope: ParameterScope.Instance, Spec: "VOLUME" } when unit == "m3" => new(Number: 1.30086m),
                    _ => throw new InvalidDataException("Wrong source identity, scope or unit: " + source)
                };
            }, "Structural Framing / Wood / 300x300")]);
            Equal(0, result.Errors.Count); Equal(2, result.Rows.Count);
            Equal(13.0086m, result.Rows[0].Value); Equal(4.3362m, result.Rows[1].Value);
            Equal("m²", result.Rows[0].Unit); Equal("m²", result.Rows[1].Unit);
            Equal("m²", template.LvCatalog.Entries[0].Unit);
            Equal(4, reads.Count); // Shared Schnittlänge is read once from the same source/unit.
            Contains("Volumen=1.30086 m3", result.Rows[1].Explanation);
            Contains("Structural Framing", result.Rows[1].Explanation);
            template.Mappings[0].Positions[1].Quantity!.Expression = "Volumen^(1/2)/Schnittlänge";
            result = QuantityTakeoff.Calculate(template, [Element("beam-42", template, (source, _) =>
                new(Number: source.Name == "Volumen" ? 1.30086m : 14.454m))]);
            Equal(1, result.Rows.Count); // The sibling is independent and still emits.
            Equal(1, result.Errors.Count); Contains("Dimension", result.Errors[0].Message);
        });
        Test("a unitless Revit NUMBER cannot acquire a physical formula result unit", () =>
        {
            var template = Template();
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = "m" };
            template.Mappings[0].Positions[0].Quantity = new() { Kind = QuantityKind.Formula, Source = null,
                Unit = "cm", Expression = "x", Variables = [new() { Name = "x", Source = Local("x", "NUMBER"), Unit = "1" }] };
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => new(Number: 100))]);
            Equal(0, result.Rows.Count);
            Equal(1, result.Errors.Count);
            Contains("Dimension", result.Errors[0].Message);
        });
        Test("unitless NUMBER or INTEGER formula values cannot become item counts or lump sums", () =>
        {
            foreach (var spec in new[] { "NUMBER", "INTEGER" })
            foreach (var unit in new[] { "St", "psch" })
            {
                var template = Template();
                template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = unit };
                template.Mappings[0].Positions[0].Quantity = new() { Kind = QuantityKind.Formula, Source = null,
                    Unit = unit, Expression = "x", Variables = [new() { Name = "x", Source = Local("x", spec), Unit = "1" }] };
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => new(Number: 100))]);
                Equal(0, result.Rows.Count);
                Equal(1, result.Errors.Count);
                Contains("Dimension", result.Errors[0].Message);
            }
        });
        Test("NUMBER or INTEGER formula variables cannot be declared with St or psch input units", () =>
        {
            foreach (var spec in new[] { "NUMBER", "INTEGER" })
            foreach (var unit in new[] { "St", "psch" })
            {
                if (UnitCatalog.AllowsFormulaInput(spec, unit) || UnitCatalog.ForFormulaInputSpec(spec).Contains(unit, StringComparer.Ordinal))
                    throw new Exception($"Formula editor still allows {spec} input unit {unit}");
                var template = Template();
                template.Mappings[0].Positions[0].Quantity = new() { Kind = QuantityKind.Formula, Source = null,
                    Unit = unit, Expression = "x", Variables = [new() { Name = "x", Source = Local("x", spec), Unit = unit }] };
                Reject<InvalidDataException>(() => QuantityTakeoff.Calculate(template,
                    [Element("e1", template, (_, _) => new(Number: 100))]));
            }
        });
        Test("physical formula variables use a common base unit before multiplication and LV conversion", () =>
        {
            var template = Template();
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = "m2" };
            template.Mappings[0].Positions[0].Quantity = new() { Kind = QuantityKind.Formula, Source = null,
                Unit = "cm2", Expression = "Length * Width", Variables = [
                    new() { Name = "Length", Source = Local("Length"), Unit = "cm" },
                    new() { Name = "Width", Source = Local("Width"), Unit = "m" }] };
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (s, u) =>
                new(Number: (s.Name, u) switch { ("Length", "cm") => 100, ("Width", "m") => 2,
                    _ => throw new Exception("Unexpected formula read") }))]);
            Equal(0, result.Errors.Count);
            Equal(2m, result.Rows.Single().Value);
            Equal("m2", result.Rows.Single().Unit);
        });
        Test("formula addition accepts matching physical dimensions and explicit literal result units", () =>
        {
            var template = Template();
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = "m" };
            template.Mappings[0].Positions[0].Quantity = new() { Kind = QuantityKind.Formula, Source = null,
                Unit = "cm", Expression = "A + B + 0.5", Variables = [
                    new() { Name = "A", Source = Local("A"), Unit = "cm" },
                    new() { Name = "B", Source = Local("B"), Unit = "m" }] };
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (s, _) =>
                new(Number: s.Name == "A" ? 100 : 2))]);
            Equal(0, result.Errors.Count);
            Equal(3.005m, result.Rows.Single().Value);
        });
        Test("formula rejects addition of unlike physical dimensions", () =>
        {
            var template = Template();
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = "m" };
            template.Mappings[0].Positions[0].Quantity = new() { Kind = QuantityKind.Formula, Source = null,
                Unit = "m", Expression = "A + B", Variables = [
                    new() { Name = "A", Source = Local("A"), Unit = "m" },
                    new() { Name = "B", Source = Local("B", "AREA"), Unit = "m2" }] };
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => new(Number: 1))]);
            Equal(0, result.Rows.Count);
            Equal(1, result.Errors.Count);
            Contains("Dimension", result.Errors[0].Message);
        });
        Test("conditions are typed ordinal AND predicates using declared units", () =>
        {
            var template = Template();
            template.Mappings[0].Conditions =
            [
                new() { Source = Local("Length"), Operand = OperandKind.Numeric, Operator = ConditionOperator.GreaterOrEqual, Number = 100, Unit = "mm" },
                new() { Source = Local("Label", "TEXT"), Operand = OperandKind.Text, Operator = ConditionOperator.Equal, Text = "Valve" },
                new() { Source = Local("Enabled", "YESNO"), Operand = OperandKind.Boolean, Operator = ConditionOperator.Equal, Boolean = true }
            ];
            var cases = new[] { (100m, "Valve", true, 1), (99m, "Valve", true, 0),
                (100m, "valve", true, 0), (100m, "Valve ", true, 0), (100m, "Valve", false, 0) };
            foreach (var (length, label, enabled, count) in cases)
            {
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (s, u) => s.Name switch
                {
                    "Length" => u == "mm" ? new(Number: length) : throw new Exception("Condition unit lost"),
                    "Label" => new(Text: label), "Enabled" => new(Boolean: enabled), _ => throw new Exception("Unexpected read")
                })]);
                Equal(0, result.Errors.Count);
                Equal(count, result.Rows.Count);
                Equal(0, result.Unmapped); // BGK/type matched, even when predicates did not.
            }
            foreach (var op in Enum.GetValues<ConditionOperator>())
            {
                template.Mappings[0].Conditions = [new() { Source = Local("Length"), Operand = OperandKind.Numeric,
                    Operator = op, Number = 10, Unit = "mm" }];
                foreach (var number in new[] { 9m, 10m, 11m })
                {
                    bool expected = op switch { ConditionOperator.Equal => number == 10, ConditionOperator.NotEqual => number != 10,
                        ConditionOperator.Greater => number > 10, ConditionOperator.GreaterOrEqual => number >= 10,
                        ConditionOperator.Less => number < 10, ConditionOperator.LessOrEqual => number <= 10, _ => false };
                    Equal(expected ? 1 : 0, QuantityTakeoff.Calculate(template,
                        [Element("e1", template, (_, _) => new(Number: number))]).Rows.Count);
                }
            }
            foreach (var operand in new[] { OperandKind.Text, OperandKind.Boolean })
            {
                template.Mappings[0].Conditions = [new() { Source = Local("x", operand == OperandKind.Text ? "TEXT" : "YESNO"),
                    Operand = operand, Operator = ConditionOperator.NotEqual, Text = "X", Boolean = true }];
                Equal(1, QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) =>
                    operand == OperandKind.Text ? new(Text: "x") : new(Boolean: false))]).Rows.Count);
                Equal(0, QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) =>
                    operand == OperandKind.Text ? new(Text: "X") : new(Boolean: true))]).Rows.Count);
            }
        });
        Test("Angle and length rules match sub-nanounit unit-conversion noise", () =>
        {
            var template = Template();
            template.Mappings[0].Conditions =
            [
                new() { Source = Local("AVA_DN"), Operand = OperandKind.Numeric, Operator = ConditionOperator.Equal, Number = 125m, Unit = "mm" },
                new() { Source = Local("AVA_Winkel", "ANGLE"), Operand = OperandKind.Numeric, Operator = ConditionOperator.Equal, Number = 30m, Unit = "deg" }
            ];
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (source, _) =>
                new(Number: source.Name == "AVA_DN" ? 125.00000000000001m : 29.999999999999996m))]);
            Equal(0, result.Errors.Count);
            Equal(1, result.Rows.Count);
            foreach (var (dn, angle) in new[] { (125.00000001m, 30m), (125m, 30.00000001m), (125m, 45m) })
            {
                result = QuantityTakeoff.Calculate(template, [Element("e1", template, (source, _) =>
                    new(Number: source.Name == "AVA_DN" ? dn : angle))]);
                Equal(0, result.Errors.Count);
                Equal(0, result.Rows.Count);
            }
            template.Mappings[0].Conditions = [new() { Source = Local("Count", "NUMBER"), Operand = OperandKind.Numeric,
                Operator = ConditionOperator.Equal, Number = 30m, Unit = "1" }];
            result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => new(Number: 30.000000000000001m))]);
            Equal(0, result.Errors.Count);
            Equal(0, result.Rows.Count); // Unitless values remain exact.
        });
        Test("Conversion tolerance does not change ordered length comparisons", () =>
        {
            var template = Template();
            var condition = new RuleCondition { Source = Local("AVA_DN"), Operand = OperandKind.Numeric,
                Operator = ConditionOperator.Greater, Number = 125m, Unit = "mm" };
            template.Mappings[0].Conditions = [condition];
            Equal(1, QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) =>
                new(Number: 125.00000000000001m))]).Rows.Count);
            condition.Operator = ConditionOperator.Less;
            Equal(1, QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) =>
                new(Number: 124.99999999999999m))]).Rows.Count);
        });
        Test("unverifiable NUMBER/INTEGER physical condition units are blocked", () =>
        {
            foreach (var (spec, unit) in new[] { ("NUMBER", "m"), ("INTEGER", "d") })
            {
                var template = Template();
                template.Mappings[0].Conditions = [new RuleCondition
                {
                    Source = Local("LängeAlsZahl", spec), Operand = OperandKind.Numeric,
                    Operator = ConditionOperator.Equal, Number = 2, Unit = unit, AllowCustomUnit = true
                }];
                string? readUnit = null;
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, requestedUnit) =>
                {
                    readUnit = requestedUnit;
                    return new(Number: 2);
                })]);
                Equal(null, readUnit);
                Equal(0, result.Rows.Count);
                Equal(1, result.Errors.Count);
            }
        });
        Test("OR joins group AND predicates with standard AND precedence", () =>
        {
            var template = Template();
            template.Mappings[0].Conditions =
            [
                new() { Source = Local("DN"), Operand = OperandKind.Numeric, Operator = ConditionOperator.Equal, Number = 150, Unit = "mm" },
                new() { Source = Local("Angle", "ANGLE"), Operand = OperandKind.Numeric, Operator = ConditionOperator.Equal, Number = 90, Unit = "deg" },
                new() { Source = Local("DN"), Operand = OperandKind.Numeric, Operator = ConditionOperator.Equal, Number = 125, Unit = "mm", JoinWithPrevious = ConditionJoin.Or },
                new() { Source = Local("Angle", "ANGLE"), Operand = OperandKind.Numeric, Operator = ConditionOperator.Equal, Number = 90, Unit = "deg" }
            ];
            TakeoffResult Calculate(decimal dn, decimal angle) => QuantityTakeoff.Calculate(template,
                [Element("e1", template, (source, _) => new(Number: source.Name == "DN" ? dn : angle))]);
            Equal(1, Calculate(150, 90).Rows.Count);
            Equal(1, Calculate(125, 90).Rows.Count);
            Equal(0, Calculate(150, 45).Rows.Count);
            Equal(0, Calculate(100, 90).Rows.Count);
            template.Mappings[0].Conditions =
            [
                new() { Source = Local("DN"), Operand = OperandKind.Numeric, Operator = ConditionOperator.Equal, Number = 150, Unit = "mm" },
                new() { Source = Local("Missing", "ANGLE"), Operand = OperandKind.Numeric, Operator = ConditionOperator.Equal, Number = 90, Unit = "deg", JoinWithPrevious = ConditionJoin.Or }
            ];
            var missing = QuantityTakeoff.Calculate(template, [Element("e1", template, (source, _) =>
                source.Name == "DN" ? new(Number: 150) : throw new InvalidDataException("Missing angle"))]);
            Equal(1, missing.Errors.Count);
            Equal(0, missing.Rows.Count); // An unvalidated OR alternative must not be hidden by the matching first branch.
        });
        Test("invalid required quantity inputs produce contextual errors rather than fabricated zero", () =>
        {
            foreach (var value in new[] { new TakeoffValue(), new(Text: "5"), new(Boolean: false),
                new(Number: 5, Text: "5"), new(Number: 5, Boolean: true) })
            {
                var template = Template();
                var position = Position(new() { Source = Local("Length"), Unit = "m" });
                template.Mappings[0].Positions.Add(position);
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => value)]);
                Equal(1, result.Rows.Count); // Valid siblings may be retained, but Errors blocks ALL export.
                Equal(2m, result.Rows[0].Value);
                var issue = result.Errors.Single();
                Equal("e1", issue.ElementId);
                Contains("e1", issue.Message);
                Contains(template.Mappings[0].Id.ToString(), issue.Message);
                Contains(position.Id.ToString(), issue.Message);
                Contains("Length", issue.Message);
            }
            foreach (var exception in new Exception[] { new InvalidDataException("missing parameter"), new OverflowException("adapter overflow") })
            {
                var template = Template();
                template.Mappings[0].Positions[0].Quantity = new() { Source = Local("Length"), Unit = "m" };
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => throw exception)]);
                Equal(0, result.Rows.Count);
                Contains(exception.Message, result.Errors.Single().Message);
            }
        });
        Test("condition failures are errors even after an earlier false AND input", () =>
        {
            foreach (var operand in Enum.GetValues<OperandKind>())
            foreach (var actual in new[] { new TakeoffValue(), new(Number: 1, Text: "x", Boolean: true) })
            {
                var template = Template();
                template.Mappings[0].Conditions =
                [
                    new() { Source = Local("False", "YESNO"), Operand = OperandKind.Boolean, Boolean = true },
                    new() { Source = Local("Missing", operand == OperandKind.Numeric ? "LENGTH" : operand == OperandKind.Text ? "TEXT" : "YESNO"),
                        Operand = operand, Unit = operand == OperandKind.Numeric ? "m" : "" }
                ];
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (s, _) =>
                    s.Name == "False" ? new(Boolean: false) : actual)]);
                Equal(0, result.Rows.Count);
                Equal(0, result.Unmapped);
                Contains(template.Mappings[0].Id.ToString(), result.Errors.Single().Message);
                Contains("Missing", result.Errors.Single().Message);
            }
            var withReaderError = Template();
            withReaderError.Mappings[0].Conditions.Add(new() { Source = Local("Missing"), Operand = OperandKind.Numeric, Unit = "m" });
            Equal(1, QuantityTakeoff.Calculate(withReaderError, [Element("e1", withReaderError)]).Errors.Count);
        });
        Test("draft quantities and failing formulas produce per-position errors", () =>
        {
            foreach (var expression in new[] { "1/0", "79228162514264337593543950335+1", "x" })
            {
                var template = Template();
                var formula = new QuantitySource { Kind = QuantityKind.Formula, Source = null, Expression = expression, Unit = "m2" };
                if (expression == "x") formula.Variables.Add(new() { Name = "x", Source = Local("Missing"), Unit = "m" });
                template.Mappings[0].Positions.Add(Position(formula));
                template.Mappings[0].Positions.Add(Position(null));
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => new())]);
                Equal(1, result.Rows.Count);
                Equal(2, result.Errors.Count);
                Contains(template.Mappings[0].Positions[1].Id.ToString(), result.Errors[0].Message);
                Contains(template.Mappings[0].Positions[2].Id.ToString(), result.Errors[1].Message);
            }
        });
        Test("all elements are examined with exact identification and separate missing/unmapped counts", () =>
        {
            var template = Template();
            var result = QuantityTakeoff.Calculate(template,
            [
                Element("valid", template), Element("leading-zero-bgk", template, bgk: "1"),
                Element("leading-zero-type", template, type: "2"), Element("space-bgk", template, bgk: "001 "),
                Element("missing-bgk", template, bgk: ""), Element("missing-type", template, type: ""),
                new("absent-both", (_, _) => new())
            ]);
            Equal(7, result.Examined);
            Equal(3, result.Unmarked);
            Equal(3, result.Unmapped);
            Equal(0, result.Errors.Count);
            Equal(1, result.Rows.Count);
            Contains("valid", result.Rows[0].Explanation);
            var empty = QuantityTakeoff.Calculate(template, []);
            Equal(0, empty.Examined); Equal(0, empty.Rows.Count);
        });
        Test("BGK assignment value with the catalog description maps back to its exact key", () =>
        {
            var template = Template();
            template.BgkCatalog = new(template.BgkCatalog.Source,
                [new("02-10", "Bodenaushub", "Category", [new("110", "Type")])]);
            template.Mappings[0].BgkKey = "02-10";
            template.Mappings[0].TypeNumber = "110";
            var result = QuantityTakeoff.Calculate(template,
                [Element("composite-bgk", template, bgk: "02-10 Bodenaushub", type: "110")]);
            Equal(1, result.Examined);
            Equal(0, result.Unmarked);
            Equal(0, result.Unmapped);
            Equal(0, result.Errors.Count);
            Equal(1, result.Rows.Count);
            Equal("lv1", result.Rows[0].LvItemId);
            Equal(2m, result.Rows[0].Value);
        });
        Test("identification reader errors and mistyped identification are not hidden as unmarked", () =>
        {
            var template = Template();
            foreach (var value in new[] { new TakeoffValue(Number: 1), new(Boolean: true), new(Text: "001", Number: 1) })
            {
                var result = QuantityTakeoff.Calculate(template, [new("bad-id", (_, _) => value)]);
                Equal(1, result.Examined); Equal(0, result.Unmarked); Equal(0, result.Unmapped);
                Equal(0, result.Rows.Count); Equal(1, result.Errors.Count);
                Contains("bad-id", result.Errors.Single().Message);
            }
            foreach (var exception in new Exception[] { new InvalidDataException("identification failed"), new OverflowException("ID overflow") })
            {
                var result = QuantityTakeoff.Calculate(template, [new("read-error", (_, _) => throw exception)]);
                Equal(0, result.Unmarked); Equal(1, result.Errors.Count);
                Contains(exception.Message, result.Errors[0].Message);
            }
        });
        Test("per-element snapshots cache by full semantic source identity plus requested unit", () =>
        {
            var template = Template();
            template.LvCatalog.Entries.Add(new("length", "boq", ["02"], "02", "Length", "", "m"));
            template.LvCatalog.Entries.Add(new("centimetre", "boq", ["03"], "03", "Centimetre", "", "cm"));
            template.LvCatalog.Entries.Add(new("number", "boq", ["04"], "04", "Number", "", "1"));
            var shared1 = Guid.NewGuid(); var shared2 = Guid.NewGuid();
            template.ParameterCatalog.Entries.AddRange([new(shared1, "Length", "LENGTH", ""), new(shared2, "Length", "LENGTH", "")]);
            ParameterSource Shared(Guid guid) => new() { Kind = ParameterKind.SharedGuid, SharedGuid = guid, Name = "Length", Spec = "LENGTH", Scope = ParameterScope.Instance };
            var sources = new[] { Local("Length"), Local("Length", scope: ParameterScope.Type), Local("length"),
                Local("Length", "NUMBER"), Shared(shared1), Shared(shared2) };
            template.Mappings[0].Conditions = [new() { Source = Local("Length"), Operand = OperandKind.Numeric, Unit = "m", Number = 1 },
                new() { Source = TemplateJson.Copy(template.BgkIdentification), Operand = OperandKind.Text, Text = "001" }];
            template.Mappings[0].Positions.Clear();
            foreach (var source in sources)
            {
                var unit = source.Spec == "NUMBER" ? "1" : "m";
                template.Mappings[0].Positions.Add(new() { LvItemId = source.Spec == "NUMBER" ? "number" : "length", Quantity = new() { Source = source, Unit = unit } });
                template.Mappings[0].Positions.Add(new() { LvItemId = source.Spec == "NUMBER" ? "number" : "length", Quantity = new() { Source = TemplateJson.Copy(source), Unit = unit } });
            }
            template.Mappings[0].Positions.Add(new() { LvItemId = "centimetre", Quantity = new() { Source = Local("Length"), Unit = "cm" } });
            template.Mappings[0].Positions.Add(new() { LvItemId = "length", Quantity = new() { Kind = QuantityKind.Formula, Source = null, Unit = "m", Expression = "x",
                Variables = [new() { Name = "x", Source = Local("Length"), Unit = "m" }] } });
            int reads = 0;
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => { reads++; return new(Number: 1); }),
                Element("e2", template, (_, _) => { reads++; return new(Number: 1); })]);
            Equal(0, result.Errors.Count);
            Equal(28, result.Rows.Count);
            Equal(14, reads); // Six identities, one extra unit, independently for each element.
        });
        Test("duplicate or empty element identifiers are rejected instead of double-counted", () =>
        {
            var template = Template();
            Reject<InvalidDataException>(() => QuantityTakeoff.Calculate(template, [Element("same", template), Element("same", template)]));
            Reject<InvalidDataException>(() => QuantityTakeoff.Calculate(template, [Element("", template)]));
            Equal(2, QuantityTakeoff.Calculate(template, [Element("e", template), Element("E", template)]).Examined);
        });
        Test("failed source reads are snapshotted with position context and no scope fallback", () =>
        {
            foreach (var exception in new Exception[] { new InvalidDataException("missing"), new OverflowException("conversion overflow") })
            {
                var template = Template();
                var source = Local("Length", scope: ParameterScope.Type);
                template.Mappings[0].Positions = [Position(new() { Source = source, Unit = "m" }),
                    Position(new() { Source = TemplateJson.Copy(source), Unit = "m" })];
                int reads = 0;
                var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (s, u) =>
                {
                    Equal(ParameterScope.Type, s.Scope); Equal("m", u);
                    reads++; throw exception;
                })]);
                Equal(1, reads);
                Equal(0, result.Rows.Count); Equal(2, result.Errors.Count);
                foreach (var issue in result.Errors) { Contains("Length", issue.Message); Contains(exception.Message, issue.Message); }
            }
        });
        Test("fractional INTEGER adapter values are type errors", () =>
        {
            var template = Template();
            template.Mappings[0].Positions[0].Quantity = new() { Source = Local("Count", "INTEGER"), Unit = "St", AllowCustomUnit = true };
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => new(Number: 1.5m))]);
            Equal(0, result.Rows.Count); Equal(1, result.Errors.Count);
            Contains("INTEGER", result.Errors[0].Message);
            Equal(-2m, QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => new(Number: -2))]).Rows.Single().Value);
        });
        Test("overlapping rules are additive and do not stop at a matching or failed rule", () =>
        {
            var template = Template();
            template.Mappings.Add(new() { BgkKey = "001", TypeNumber = "02", Positions = [Position(Constant(3)), Position(Constant(4))] });
            Equal(true, TemplateValidator.Validate(template).Any(d => d.Code == "MULTIPLE_RULES"));
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template)]);
            Equal("2,3,4", string.Join(",", result.Rows.Select(r => r.Value)));
            Equal(0, result.Errors.Count);
            Contains(template.Mappings[1].Id.ToString(), result.Rows[1].Explanation);
            template.Mappings[0].Positions[0].Quantity = null;
            result = QuantityTakeoff.Calculate(template, [Element("e1", template)]);
            Equal(1, result.Errors.Count); Equal(2, result.Rows.Count);
        });
        Test("deterministic output preserves zero and negative amounts without mutation or rounding", () =>
        {
            var template = Template();
            template.LvCatalog.Entries.Add(new("lv2", "boq", ["02"], "02", "Length", "", "m"));
            template.Mappings[0].Positions.Add(new() { LvItemId = "lv2", Quantity = new() { Source = Local("Length"), Unit = "m" } });
            template.Mappings[0].Positions.Add(new() { LvItemId = "lv2", Quantity = new() { Kind = QuantityKind.Formula, Source = null, Expression = "-1.125", Unit = "m" } });
            template.Mappings[0].Positions.Add(new() { LvItemId = "lv2", Quantity = new() { Kind = QuantityKind.Formula, Source = null, Expression = "-0", Unit = "m" } });
            var original = TemplateJson.Serialize(template);
            var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                TakeoffResult? previous = null;
                foreach (var culture in new[] { "en-US", "de-DE", "fr-FR" })
                {
                    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                    var result = QuantityTakeoff.Calculate(template, [Element("b", template, (_, _) => new(Number: 0)), Element("a", template, (_, _) => new(Number: -0.123456789m))]);
                    Equal(8, result.Rows.Count); Equal(0, result.Errors.Count);
                    Equal(0m, result.Rows[1].Value); Equal(-1.125m, result.Rows[2].Value); Equal(0m, result.Rows[3].Value);
                    Equal(-0.123456789m, result.Rows[5].Value);
                    Contains("b", result.Rows[0].Explanation); Contains("a", result.Rows[4].Explanation);
                    if (previous != null) Equal(true, previous.Rows.SequenceEqual(result.Rows));
                    previous = result;
                }
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
            Equal(original, TemplateJson.Serialize(template));
        });
        Test("unknown and process-critical reader failures propagate unchanged", () =>
        {
            var template = Template();
            Reject<OutOfMemoryException>(() => QuantityTakeoff.Calculate(template, [new("id", (_, _) => throw new OutOfMemoryException("test signal"))]));
            template.Mappings[0].Conditions.Add(new() { Source = Local("Condition"), Operand = OperandKind.Numeric, Unit = "m" });
            Reject<InvalidOperationException>(() => QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => throw new InvalidOperationException("test signal"))]));
            template.Mappings[0].Conditions.Clear();
            template.Mappings[0].Positions[0].Quantity = new() { Source = Local("Length"), Unit = "m" };
            Reject<InvalidOperationException>(() => QuantityTakeoff.Calculate(template, [Element("e1", template, (_, _) => throw new InvalidOperationException("test signal"))]));
        });
        Test("mixed sibling quantities use independent LV identities and never borrow a sibling source", () =>
        {
            var template = Template();
            template.LvCatalog.Entries.Add(new("lv2", "boq", ["02"], "02", "Length", "", "m"));
            template.LvCatalog.Entries.Add(new("lv3", "boq", ["03"], "03", "Formula", "", "m2"));
            template.Mappings[0].Conditions = [new() { Source = Local("Length"), Operand = OperandKind.Numeric,
                Operator = ConditionOperator.Greater, Number = 0, Unit = "m" },
                new() { Source = Local("Enabled", "YESNO"), Operand = OperandKind.Boolean, Boolean = true }];
            template.Mappings[0].Positions.Add(new() { LvItemId = "lv2", Quantity = new() { Source = Local("Length"), Unit = "cm" } });
            template.Mappings[0].Positions.Add(new() { LvItemId = "lv3", Quantity = new() { Kind = QuantityKind.Formula,
                Source = null, Expression = "L*W", Unit = "m2", Variables = [new() { Name = "L", Source = Local("Length"), Unit = "m" },
                    new() { Name = "W", Source = Local("Width"), Unit = "m" }] } });
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template, (s, u) => s.Name switch
            {
                "Enabled" => new(Boolean: true), "Length" => new(Number: u == "cm" ? 200 : 2),
                "Width" => new(Number: 3), _ => throw new InvalidDataException("unexpected source")
            })]);
            Equal(0, result.Errors.Count);
            Equal("lv1,lv2,lv3", string.Join(",", result.Rows.Select(r => r.LvItemId)));
            Equal(2m, result.Rows[0].Value); Equal("St", result.Rows[0].Unit);
            Equal(2m, result.Rows[1].Value); Equal("m", result.Rows[1].Unit);
            Contains("200 cm", result.Rows[1].Explanation);
            Equal(6m, result.Rows[2].Value); Equal("m2", result.Rows[2].Unit);
        });
        Test("built-in identity cache distinguishes BuiltInId even with equal display names", () =>
        {
            var template = Template();
            template.BgkIdentification.Name = template.TypeIdentification.Name = "Identity";
            template.Mappings[0].Conditions.Add(new() { Source = TemplateJson.Copy(template.TypeIdentification),
                Operand = OperandKind.Text, Text = "02" });
            var result = QuantityTakeoff.Calculate(template, [Element("e1", template)]);
            Equal(1, result.Rows.Count); Equal(0, result.Errors.Count);
        });
        Test("quantity explanations retain exact GUID scope spec and formula binding provenance", () =>
        {
            var template = Template();
            template.LvCatalog.Entries.Add(new("cm", "boq", ["02"], "02", "Length", "", "cm"));
            template.LvCatalog.Entries.Add(new("m", "boq", ["03"], "03", "Length", "", "m"));
            var guid = Guid.NewGuid();
            template.ParameterCatalog.Entries.Add(new(guid, "MeasuredLength", "LENGTH", ""));
            var shared = new ParameterSource { Kind = ParameterKind.SharedGuid, SharedGuid = guid,
                Name = "MeasuredLength", Spec = "LENGTH", Scope = ParameterScope.Type };
            template.Mappings[0].Positions[0].LvItemId = "cm";
            template.Mappings[0].Positions[0].Quantity = new() { Source = shared, Unit = "cm" };
            template.Mappings[0].Positions.Add(new() { LvItemId = "m", Quantity = new() { Kind = QuantityKind.Formula, Source = null,
                Expression = "L*2", Unit = "m", Variables = [new() { Name = "L", Source = shared, Unit = "m" }] } });
            var result = QuantityTakeoff.Calculate(template, [Element("42", template, (_, unit) => new(Number: unit == "cm" ? 125 : 1.25m))]);
            Equal(0, result.Errors.Count);
            foreach (var line in result.Rows)
            {
                Contains(guid.ToString(), line.Explanation);
                Contains("LENGTH", line.Explanation);
                Contains("Type", line.Explanation);
            }
            Contains("L=1.25 m", result.Rows[1].Explanation);
        });
        Test("optional document and UniqueId provenance is retained verbatim without changing numeric element identity", () =>
        {
            var template = Template();
            var identity = "Dokument: C:\\Models\\A&B <building>.rvt; UniqueId: full-unique-id-00000042";
            var original = Element("42", template);
            var constructor = typeof(TakeoffElement).GetConstructor([typeof(string), typeof(Func<ParameterSource, string, TakeoffValue>), typeof(string)]);
            Equal(true, constructor != null);
            var element = (TakeoffElement)constructor!.Invoke([original.Id, original.Read, identity]);
            var result = QuantityTakeoff.Calculate(template, [element]);
            Equal("42", element.Id);
            Equal(0, result.Errors.Count);
            Contains(identity, result.Rows.Single().Explanation);
            Contains("Element 42;", result.Rows.Single().Explanation);
        });
        Console.WriteLine($"{passed} passed; {failed} failed");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }

    private static IEnumerable<TakeoffElement> ForbiddenElements()
    {
        throw new Exception("Must validate before enumeration.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
    private static AvaTemplate Template()
    {
        var stamp = new SourceStamp("fixture", "test", "");
        return new()
        {
            ReviewScope = "all host instances",
            BgkCatalog = new(stamp, [new("001", "Group", "Category", [new("02", "Type")])]),
            LvCatalog = new(stamp, [new("lv1", "boq", ["01"], "01", "Item", "", "St")]),
            Mappings = [new() { BgkKey = "001", TypeNumber = "02", Positions = [Position(Constant(2))] }]
        };
    }

    private static MappingPosition Position(QuantitySource? quantity) => new() { LvItemId = "lv1", Quantity = quantity };
    private static QuantitySource Constant(decimal value) => new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = value, Unit = "St" };
    private static ParameterSource Local(string name, string spec = "LENGTH", ParameterScope scope = ParameterScope.Instance) =>
        new() { Kind = ParameterKind.LocalFamily, Name = name, Spec = spec, Scope = scope };
    private static TakeoffElement Element(string id, AvaTemplate template, Func<ParameterSource, string, TakeoffValue>? read = null,
        string bgk = "001", string type = "02") => new(id, (source, unit) =>
        {
            if (ReferenceEquals(source, template.BgkIdentification)) { Equal("", unit); return new(Text: bgk); }
            if (ReferenceEquals(source, template.TypeIdentification)) { Equal("", unit); return new(Text: type); }
            return read?.Invoke(source, unit) ?? throw new InvalidDataException("Missing " + source.Name);
        });
    private static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}");
    }
    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal)) throw new Exception($"Missing '{expected}' in '{actual}'");
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
