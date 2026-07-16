"""Adversarial / fault-injection tests for server/utils.py."""

from __future__ import annotations

from decimal import Decimal, InvalidOperation

import pytest

from server.utils import (
    BoundingBox,
    OCRLine,
    clean_amount,
    clean_date,
    collapse_text,
    extract_amount,
    extract_date,
    normalize_text,
    normalize_text_for_output,
    reconcile_amounts,
    validate_amounts,
)


# ── extract_amount ────────────────────────────────────────────────────────────


class TestExtractAmount:
    """Adversarial inputs for extract_amount (and clean_amount alias)."""

    def test_oversized_decimal(self):
        """A gargantuan number from garbled OCR must not crash with
        decimal.InvalidOperation on the quantize() call."""
        # This is the user's reproduction case
        result = extract_amount("999999999999999999999999999999999999999.99")
        assert result is None

    def test_oversized_integer(self):
        """Very long integer string must not crash."""
        result = extract_amount("9" * 100)
        assert result is None

    def test_empty_string(self):
        assert extract_amount("") is None

    def test_none(self):
        assert extract_amount(None) is None

    def test_whitespace_only(self):
        assert extract_amount("   \t\n  ") is None

    def test_garbage_text(self):
        """Completely non-numeric text returns None."""
        assert extract_amount("hello world") is None

    def test_mixed_garbage_and_number(self):
        """Garbage prefix/suffix with a real number in the middle."""
        result = extract_amount("xyz123.45abc")
        assert result is not None
        assert float(result) == 123.45

    def test_tnd_currency_stripped(self):
        assert float(extract_amount("1 234,56 TND")) == 1234.56  # noqa: PLR2004

    def test_dt_currency_stripped(self):
        assert float(extract_amount("500 DT")) == 500.0  # noqa: PLR2004

    def test_euro_symbol(self):
        assert float(extract_amount("99,99 €")) == 99.99  # noqa: PLR2004

    def test_dollar_symbol(self):
        assert float(extract_amount("$ 1,234.56")) == 1234.56  # noqa: PLR2004

    def test_mixed_comma_and_dot_french(self):
        """French style: 1.234,56 → 1234.56."""
        result = extract_amount("1.234,56")
        assert result is not None
        assert float(result) == 1234.56  # noqa: PLR2004

    def test_mixed_comma_and_dot_english(self):
        """English style: 1,234.56 → 1234.56."""
        result = extract_amount("1,234.56")
        assert result is not None
        assert float(result) == 1234.56  # noqa: PLR2004

    def test_thousands_separator_no_decimal(self):
        """Multiple commas = thousands separators (English style)."""
        assert float(extract_amount("1,234,567")) == 1_234_567.0  # noqa: PLR2004

    def test_negative_amount(self):
        result = extract_amount("-123.45")
        assert result is not None
        assert float(result) == -123.45  # noqa: PLR2004

    def test_negative_amount_french(self):
        result = extract_amount("-123,45")
        assert result is not None
        assert float(result) == -123.45  # noqa: PLR2004

    def test_unicode_digits(self):
        """Arabic-Indic digits should not be parseable by Decimal directly,
        and OCR should yield Latin digits, but if they slip through they
        should not crash."""
        result = extract_amount("١٢٣٫٤٥")  # Arabic-Indic digits
        # May or may not parse, but must not crash
        assert result is None or isinstance(result, str)

    def test_trailing_text_after_number(self):
        assert float(extract_amount("1500 DA")) == 1500.0  # noqa: PLR2004

    def test_number_with_leading_zeros(self):
        result = extract_amount("0000123.45")
        assert result is not None
        assert float(result) == 123.45  # noqa: PLR2004

    # ── clean_amount alias ────────────────────────────────────────────────────

    def test_clean_amount_alias(self):
        assert clean_amount("42.00") == extract_amount("42.00")

    def test_clean_amount_oversized(self):
        assert clean_amount("999999999999999999999999999999999999999.99") is None

    def test_very_large_but_valid(self):
        """A large number within Decimal precision should still work."""
        result = extract_amount("9999999999999999999.99")
        assert result is not None
        assert float(result) == 9_999_999_999_999_999_999.99


# ── extract_date / clean_date ─────────────────────────────────────────────────


class TestExtractDate:
    def test_empty(self):
        assert extract_date("") is None

    def test_none(self):
        assert extract_date(None) is None

    def test_garbage(self):
        assert extract_date("not a date") is None

    def test_dd_mm_yyyy(self):
        assert extract_date("15/03/2024") == "2024-03-15"

    def test_dd_mm_yy(self):
        assert extract_date("15/03/24") == "2024-03-15"

    def test_dd_mm_yyyy_dash(self):
        assert extract_date("15-03-2024") == "2024-03-15"

    def test_french_month_name(self):
        assert extract_date("15 mars 2024") == "2024-03-15"

    def test_french_month_accented(self):
        assert extract_date("15 décembre 2024") == "2024-12-15"

    def test_french_month_alternative(self):
        assert extract_date("15 février 2024") == "2024-02-15"

    def test_invalid_date_value(self):
        """Day 99 is invalid and must not crash."""
        assert extract_date("99/99/2024") is None

    def test_invalid_month(self):
        assert extract_date("01/13/2024") is None

    def test_date_embedded_in_text(self):
        assert extract_date("Invoice date: 10/05/2023") == "2023-05-10"

    def test_unicode_date_chars(self):
        assert extract_date("2024—03—15") is None  # em-dash separators

    def test_clean_date_alias(self):
        assert clean_date("01/01/2024") == extract_date("01/01/2024")


# ── validate_amounts ───────────────────────────────────────────────────────────


class TestValidateAmounts:
    """Validation and correction of monetary amounts (HT, TVA, Taxe, TTC)."""

    def test_all_none(self):
        """No amounts → no changes."""
        fields = {k: None for k in ["montant_ht", "montant_tva", "montant_taxe", "montant_ttc"]}
        result = validate_amounts(fields)
        assert result == fields

    def test_only_one_amount(self):
        """Only one amount found → cannot cross-validate, unchanged."""
        fields = {
            "montant_ht": "1250.000",
            "montant_tva": None,
            "montant_taxe": None,
            "montant_ttc": None,
        }
        result = validate_amounts(fields)
        assert result == fields

    def test_ht_and_tva_derive_ttc(self):
        """HT and TVA present → derive TTC."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": None,
        }
        result = validate_amounts(fields)
        assert result["montant_ht"] == "1000.000"
        assert result["montant_tva"] == "200.000"
        assert result["montant_ttc"] == "1200.000"

    def test_ht_and_ttc_derive_tva(self):
        """HT and TTC present → derive TVA."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": None,
            "montant_taxe": None,
            "montant_ttc": "1200.000",
        }
        result = validate_amounts(fields)
        assert result["montant_ht"] == "1000.000"
        assert result["montant_tva"] == "200.000"
        assert result["montant_ttc"] == "1200.000"

    def test_tva_and_ttc_derive_ht(self):
        """TVA and TTC present → derive HT."""
        fields = {
            "montant_ht": None,
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": "1200.000",
        }
        result = validate_amounts(fields)
        assert result["montant_ht"] == "1000.000"
        assert result["montant_tva"] == "200.000"
        assert result["montant_ttc"] == "1200.000"

    def test_all_three_consistent(self):
        """All three found and consistent → no changes."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": "1200.000",
        }
        result = validate_amounts(fields)
        assert result == fields

    def test_all_three_consistent_with_taxe(self):
        """All three found with taxe → consistent, no changes."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": "50.000",
            "montant_ttc": "1250.000",
        }
        result = validate_amounts(fields)
        assert result == fields

    def test_ht_equals_tva_duplication(self):
        """HT and TVA are the same value (duplication error) → derive TVA from TTC - HT."""
        fields = {
            "montant_ht": "1250.000",
            "montant_tva": "1250.000",  # Duplicate of HT (error)
            "montant_taxe": None,
            "montant_ttc": "1500.000",
        }
        result = validate_amounts(fields)
        assert result["montant_ht"] == "1250.000"
        assert result["montant_tva"] == "250.000"  # Corrected: 1500 - 1250
        assert result["montant_ttc"] == "1500.000"

    def test_ttc_equals_ht_duplication(self):
        """TTC and HT are the same value (duplication error) → derive TTC from HT + TVA."""
        fields = {
            "montant_ht": "1250.000",
            "montant_tva": "250.000",
            "montant_taxe": None,
            "montant_ttc": "1250.000",  # Duplicate of HT (error)
        }
        result = validate_amounts(fields)
        assert result["montant_ht"] == "1250.000"
        assert result["montant_tva"] == "250.000"
        assert result["montant_ttc"] == "1500.000"  # Corrected: 1250 + 250

    def test_ttc_equals_tva_duplication(self):
        """TTC and TVA are the same value (duplication error) → derive TTC."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": "200.000",  # Duplicate of TVA (error)
        }
        result = validate_amounts(fields)
        assert result["montant_ht"] == "1000.000"
        assert result["montant_tva"] == "200.000"
        assert result["montant_ttc"] == "1200.000"  # Corrected: 1000 + 200

    def test_inconsistent_no_duplication(self):
        """All three found but inconsistent with no obvious duplication → keep HT and TVA, derive TTC."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "150.000",  # Should be 200 for consistent 1200 TTC
            "montant_taxe": None,
            "montant_ttc": "1200.000",
        }
        result = validate_amounts(fields)
        assert result["montant_ht"] == "1000.000"
        assert result["montant_tva"] == "150.000"  # Kept as-is
        assert result["montant_ttc"] == "1150.000"  # Corrected: 1000 + 150

    def test_with_taxe_present(self):
        """Taxe present → included in TTC calculation."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": "50.000",
            "montant_ttc": None,
        }
        result = validate_amounts(fields)
        assert result["montant_ttc"] == "1250.000"  # 1000 + 200 + 50

    def test_french_format_amounts(self):
        """Amounts in French format (1 250,00) should be parsed correctly."""
        fields = {
            "montant_ht": "1 000,00",
            "montant_tva": "200,00",
            "montant_taxe": None,
            "montant_ttc": None,
        }
        result = validate_amounts(fields)
        assert result["montant_ttc"] == "1200.000"

    def test_derived_tva_not_negative(self):
        """If derived TVA would be negative, don't correct."""
        fields = {
            "montant_ht": "1500.000",
            "montant_tva": None,
            "montant_taxe": None,
            "montant_ttc": "1200.000",  # Less than HT — data error
        }
        result = validate_amounts(fields)
        assert result["montant_tva"] is None  # Not set (would be -300)

    def test_small_rounding_difference(self):
        """Small rounding difference (≤ €0.50) should be considered consistent."""
        fields = {
            "montant_ht": "100.000",
            "montant_tva": "20.000",
            "montant_taxe": None,
            "montant_ttc": "120.010",  # Off by €0.01, should be kept as-is
        }
        result = validate_amounts(fields)
        assert result == fields

    def test_large_rounding_difference(self):
        """Large difference (> €0.50) should trigger correction."""
        fields = {
            "montant_ht": "100.000",
            "montant_tva": "20.000",
            "montant_taxe": None,
            "montant_ttc": "130.000",  # Off by €10
        }
        result = validate_amounts(fields)
        assert result["montant_ttc"] == "120.000"  # Corrected: 100 + 20

    def test_non_amount_fields_preserved(self):
        """Non-amount fields should not be modified."""
        fields = {
            "numero_facture": "INV-001",
            "date": "2024-03-15",
            "fournisseur": "Supplier SAS",
            "client": "Client SARL",
            "montant_ht": "1250.000",
            "montant_tva": "250.000",
            "montant_taxe": None,
            "montant_ttc": None,
        }
        result = validate_amounts(fields)
        assert result["numero_facture"] == "INV-001"
        assert result["date"] == "2024-03-15"
        assert result["fournisseur"] == "Supplier SAS"
        assert result["client"] == "Client SARL"
        assert result["montant_ttc"] == "1500.000"

    def test_all_fields_present_consistent(self):
        """All amount fields present and consistent."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": "50.000",
            "montant_ttc": "1250.000",
        }
        result = validate_amounts(fields)
        assert result == fields

    def test_no_amount_fields_at_all(self):
        """No amount fields in the dict at all — shouldn't crash."""
        fields = {"numero_facture": "INV-001"}
        result = validate_amounts(fields)
        assert result == fields

    def test_malformed_amount_string(self):
        """Malformed amount strings should be treated as None."""
        fields = {
            "montant_ht": "garbage",
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": None,
        }
        result = validate_amounts(fields)
        # Only TVA is parseable (< 2 available), no change
        assert result["montant_ht"] == "garbage"  # Preserved as-is
        assert result["montant_tva"] == "200.000"
        assert result["montant_ttc"] is None


# ── reconcile_amounts ─────────────────────────────────────────────────────────


class TestReconcileAmounts:
    """Tests for the arithmetic reconciliation pass."""

    def test_ht_and_tva_compute_ttc(self):
        """HT + TVA present, TTC missing → compute TTC, mark as computed."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": None,
        }
        confs = {"montant_ht": 0.9, "montant_tva": 0.85}
        result, computed, mismatch = reconcile_amounts(fields, confs)
        assert result["montant_ttc"] == "1200.000"
        assert "montant_ttc" in computed
        assert not mismatch

    def test_ht_and_ttc_compute_tva(self):
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": None,
            "montant_taxe": None,
            "montant_ttc": "1200.000",
        }
        result, computed, mismatch = reconcile_amounts(fields, {})
        assert result["montant_tva"] == "200.000"
        assert "montant_tva" in computed
        assert not mismatch

    def test_tva_and_ttc_compute_ht(self):
        fields = {
            "montant_ht": None,
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": "1200.000",
        }
        result, computed, mismatch = reconcile_amounts(fields, {})
        assert result["montant_ht"] == "1000.000"
        assert "montant_ht" in computed
        assert not mismatch

    def test_all_three_consistent_no_change(self):
        """All 3 amounts present and arithmetic consistent → no change, not computed."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": "1200.000",
        }
        result, computed, mismatch = reconcile_amounts(fields, {})
        assert result == fields
        assert len(computed) == 0
        assert not mismatch

    def test_all_three_present_inconsistent_flags_mismatch(self):
        """All 3 present but arithmetic doesn't match → flag has_mismatch, do NOT overwrite."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": "9999.000",  # Way off
        }
        result, computed, mismatch = reconcile_amounts(fields, {})
        assert result == fields  # No overwrites
        assert len(computed) == 0
        assert mismatch  # Flagged for user review

    def test_none_or_one_amount_no_op(self):
        """Fewer than 2 amounts → no changes."""
        fields = {"montant_ht": "1000.000", "montant_tva": None, "montant_taxe": None, "montant_ttc": None}
        result, computed, mismatch = reconcile_amounts(fields, {})
        assert result == fields
        assert len(computed) == 0
        assert not mismatch

    def test_negative_derivation_guard(self):
        """Deriving a negative amount should be prevented."""
        fields = {
            "montant_ht": "1500.000",
            "montant_tva": None,
            "montant_taxe": None,
            "montant_ttc": "1000.000",  # Less than HT -> TVA would be negative
        }
        result, computed, mismatch = reconcile_amounts(fields, {})
        assert result["montant_tva"] is None  # Not computed (would be negative)
        assert len(computed) == 0

    def test_taxe_included_in_ttc_computation(self):
        """Taxe should be included in TTC computation."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": "50.000",
            "montant_ttc": None,
        }
        result, computed, mismatch = reconcile_amounts(fields, {})
        assert result["montant_ttc"] == "1250.000"  # 1000 + 200 + 50
        assert "montant_ttc" in computed

    def test_consistent_with_taxe(self):
        """All 4 amounts present and consistent → no changes."""
        fields = {
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": "50.000",
            "montant_ttc": "1250.000",
        }
        result, computed, mismatch = reconcile_amounts(fields, {})
        assert result == fields
        assert len(computed) == 0
        assert not mismatch

    def test_non_amount_fields_preserved(self):
        """Non-amount fields should not be modified."""
        fields = {
            "numero_facture": "INV-001",
            "date": "2024-03-15",
            "montant_ht": "1000.000",
            "montant_tva": "200.000",
            "montant_taxe": None,
            "montant_ttc": None,
        }
        result, computed, mismatch = reconcile_amounts(fields, {})
        assert result["numero_facture"] == "INV-001"
        assert result["date"] == "2024-03-15"
        assert result["montant_ttc"] == "1200.000"
        assert "montant_ttc" in computed


# ── normalize_text / collapse_text / normalize_text_for_output ────────────────


class TestTextNormalization:
    def test_collapse_multiple_spaces(self):
        assert collapse_text("hello   world") == "hello world"

    def test_collapse_tabs(self):
        assert collapse_text("hello\t\tworld") == "hello world"

    def test_collapse_non_breaking_space(self):
        assert collapse_text("hello\u00a0world") == "hello world"

    def test_normalize_removes_accents(self):
        result = normalize_text("décembre")
        assert "é" not in result

    def test_normalize_to_ascii(self):
        assert normalize_text("café") == "cafe"

    def test_normalize_for_output_strips_noise(self):
        assert normalize_text_for_output("  : Hello -  ") == "Hello"

    def test_normalize_for_output_empty(self):
        assert normalize_text_for_output("") == ""


# ── BoundingBox ───────────────────────────────────────────────────────────────


class TestBoundingBox:
    def test_from_points(self):
        bb = BoundingBox.from_points([[10, 20], [10, 50], [40, 20], [40, 50]])
        assert bb.x1 == 10.0
        assert bb.y1 == 20.0
        assert bb.x2 == 40.0
        assert bb.y2 == 50.0

    def test_width_height(self):
        bb = BoundingBox(10.0, 20.0, 50.0, 60.0)
        assert bb.width == 40.0  # noqa: PLR2004
        assert bb.height == 40.0  # noqa: PLR2004

    def test_center(self):
        bb = BoundingBox(10.0, 20.0, 30.0, 40.0)
        assert bb.center_x == 20.0  # noqa: PLR2004
        assert bb.center_y == 30.0  # noqa: PLR2004

    def test_horizontal_overlap(self):
        a = BoundingBox(0, 0, 10, 10)
        b = BoundingBox(5, 0, 15, 10)
        assert a.horizontal_overlap(b) == 5.0  # noqa: PLR2004

    def test_no_horizontal_overlap(self):
        a = BoundingBox(0, 0, 10, 10)
        b = BoundingBox(20, 0, 30, 10)
        assert a.horizontal_overlap(b) == 0.0

    def test_vertical_gap(self):
        a = BoundingBox(0, 0, 10, 10)
        b = BoundingBox(0, 20, 10, 30)
        assert a.vertical_gap(b) == 10.0  # noqa: PLR2004

    def test_horizontal_gap(self):
        a = BoundingBox(0, 0, 10, 10)
        b = BoundingBox(20, 0, 30, 10)
        assert a.horizontal_gap(b) == 10.0  # noqa: PLR2004

    def test_overlapping_gap_is_zero(self):
        a = BoundingBox(0, 0, 20, 10)
        b = BoundingBox(10, 0, 30, 10)
        assert a.horizontal_gap(b) == 0.0

    def test_frozen_dataclass(self):
        bb = BoundingBox(1.0, 2.0, 3.0, 4.0)
        with pytest.raises(Exception):
            bb.x1 = 99.0  # type: ignore[misc]
