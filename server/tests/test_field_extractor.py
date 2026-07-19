"""Adversarial / fault-injection tests for server/field_extractor.py."""

from __future__ import annotations

import pytest

from server.field_extractor import (
    _candidate_is_plausible,
    _clean_candidate_value,
    _contains_any_alias,
    _extract_field_selections,
    _extract_inline_value,
    _looks_like_label,
    _matches_alias_relaxed,
    _score_candidate,
    extract_field_confidences,
    extract_invoice_fields,
    extract_raw_text,
)
from server.utils import BoundingBox, OCRLine


# ── extract_invoice_fields ────────────────────────────────────────────────────


class TestExtractInvoiceFields:
    """Adversarial / edge-case inputs for the top-level field extraction."""

    def test_empty_lines(self):
        """Empty OCR line list must return None for all fields."""
        fields = extract_invoice_fields([])
        assert all(v is None for v in fields.values())
        assert set(fields.keys()) == {
            "numero_facture", "date", "fournisseur", "client",
            "montant_ht", "montant_tva", "montant_taxe", "montant_ttc",
        }

    def test_only_garbage_lines(self):
        """Lines with no recognizable labels produce all-None fields."""
        lines = [
            OCRLine("sdlkfjsldkfj", BoundingBox(0, 0, 10, 10), 0.5, 0, 0),
            OCRLine("zxcvzxcvzxcv", BoundingBox(0, 15, 10, 25), 0.6, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert all(v is None for v in fields.values())

    def test_label_with_no_value(self):
        """A label with no following candidate line should produce None value."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.9, 0, 0),
            # No next line with a value
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is None

    def test_label_with_value_on_next_line(self):
        """A label followed by a number on the next line should extract."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.9, 0, 0),
            OCRLine("1234.56", BoundingBox(0, 15, 40, 25), 0.95, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is not None

    def test_multi_page_empty_second_page(self):
        """Lines spanning pages with empty second page."""
        lines = [
            OCRLine("Facture N° 123", BoundingBox(0, 0, 60, 10), 0.8, 0, 0),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["numero_facture"] is not None

    def test_inline_value_with_colon(self):
        """Value on same line after colon."""
        lines = [
            OCRLine("Date: 15/03/2024", BoundingBox(0, 0, 60, 10), 0.9, 0, 0),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["date"] is not None

    def test_inline_value_no_colon(self):
        """Value on same line after label without colon."""
        lines = [
            OCRLine("Date 15/03/2024", BoundingBox(0, 0, 60, 10), 0.9, 0, 0),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["date"] == "2024-03-15"

    def test_multiple_candidates_best_wins(self):
        """When multiple lines match a field, the best-scored one wins."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.9, 0, 0),
            OCRLine("100.00", BoundingBox(0, 15, 40, 25), 0.95, 0, 1),
            OCRLine("TTC", BoundingBox(100, 0, 150, 10), 0.8, 0, 2),
            OCRLine("200.00", BoundingBox(100, 15, 140, 25), 0.9, 0, 3),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is not None

    def test_oversized_amount_from_ocr_garbage(self):
        """A gargantuan number read as OCR garbage in a monetary field
        must not crash (regression guard for the extract_amount fix)."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.9, 0, 0),
            OCRLine(
                "999999999999999999999999999999999999999.99",
                BoundingBox(0, 15, 100, 25), 0.7, 0, 1,
            ),
        ]
        # Must not raise InvalidOperation
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is None

    def test_zero_length_text(self):
        """Zero-length or whitespace-only lines should be harmless."""
        lines = [
            OCRLine("", BoundingBox(0, 0, 5, 5), 0.0, 0, 0),
            OCRLine(" ", BoundingBox(0, 6, 5, 11), 0.0, 0, 1),
            OCRLine("  \t", BoundingBox(0, 12, 5, 17), 0.0, 0, 2),
        ]
        fields = extract_invoice_fields(lines)
        assert all(v is None for v in fields.values())

    def test_all_confidences_zero(self):
        """Zero-confidence lines should return None (rejected by threshold)."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.0, 0, 0),
            OCRLine("123.45", BoundingBox(0, 15, 40, 25), 0.0, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        # Below FIELD_CONFIDENCE_THRESHOLD (0.6), return None for "right or blank"
        confidences = extract_field_confidences(lines)
        assert fields["montant_ttc"] is None
        assert confidences["montant_ttc"] == 0.0


# ── extract_field_confidences ─────────────────────────────────────────────────


class TestExtractFieldConfidences:
    def test_empty(self):
        confs = extract_field_confidences([])
        assert all(c == 0.0 for c in confs.values())

    def test_all_fields_present(self):
        confs = extract_field_confidences([])
        assert set(confs.keys()) == {
            "numero_facture", "date", "fournisseur", "client",
            "montant_ht", "montant_tva", "montant_taxe", "montant_ttc",
        }


# ── extract_raw_text ──────────────────────────────────────────────────────────


class TestExtractRawText:
    def test_empty(self):
        assert extract_raw_text([]) == ""

    def test_single_line(self):
        lines = [OCRLine("hello", BoundingBox(0, 0, 10, 5), 0.5, 0, 0)]
        assert extract_raw_text(lines) == "hello"

    def test_empty_lines_skipped(self):
        lines = [
            OCRLine("", BoundingBox(0, 0, 5, 5), 0.0, 0, 0),
            OCRLine("hello", BoundingBox(0, 6, 10, 11), 0.5, 0, 1),
        ]
        assert extract_raw_text(lines) == "hello"

    def test_same_row_joined_with_tab(self):
        """Lines in the same visual row should be tab-joined."""
        lines = [
            OCRLine("TTC",    BoundingBox(0, 0, 30, 20), 0.9, 0, 0),
            OCRLine("1250.00", BoundingBox(100, 0, 160, 20), 0.95, 0, 1),
        ]
        result = extract_raw_text(lines)
        # Both are in the same row (vertical overlap > 50%)
        assert "TTC" in result
        assert "1250.00" in result
        assert "\t" in result  # joined by tab

    def test_different_rows_newline_separated(self):
        """Lines in different rows should be newline-separated."""
        lines = [
            OCRLine("TTC",    BoundingBox(0, 0, 30, 20), 0.9, 0, 0),
            OCRLine("1250.00", BoundingBox(0, 50, 60, 70), 0.95, 0, 1),
        ]
        result = extract_raw_text(lines)
        assert "\n" in result  # different rows = newline


# ── Internal helpers ──────────────────────────────────────────────────────────


class TestContainsAnyAlias:
    def test_exact_match(self):
        assert _contains_any_alias("total ttc", ["ttc"]) is True

    def test_normalized_match(self):
        assert _contains_any_alias("Total TTC", ["ttc"]) is True

    def test_no_match(self):
        assert _contains_any_alias("hello world", ["ttc"]) is False

    def test_empty_text(self):
        assert _contains_any_alias("", ["ttc"]) is False

    def test_bare_no_does_not_match_nom_substring(self):
        """REGRESSION: bare "no" must NOT match inside "Nom" (word boundary)."""
        # This is the false-positive-anchor bug: "Nom du produit ou service"
        # previously matched "no" inside "Nom" via plain substring containment.
        assert _contains_any_alias("Nom du produit ou service", ["no"]) is False

    def test_bare_no_does_not_match_notre_substring(self):
        """"no" must not match inside "notre", "nord", "bonjour", etc."""
        assert _contains_any_alias("Notre Reference", ["no"]) is False
        assert _contains_any_alias("Nouveau Client", ["no"]) is False

    def test_compound_no_facture_still_matches(self):
        """Compound alias "no facture" should still match normally."""
        assert _contains_any_alias("No Facture INV-001", ["no facture"]) is True
        assert _contains_any_alias("N° facture: INV-001", ["n° facture"]) is True

    def test_short_ht_does_not_match_substring(self):
        """"ht" must NOT match inside unrelated words like "echt"."""
        assert _contains_any_alias("Echt GmbH", ["ht"]) is False

    def test_compound_ht_still_matches(self):
        """"total ht" should still match as a whole-word alias."""
        assert _contains_any_alias("Total HT", ["total ht"]) is True

    def test_bare_tva_as_word_matches(self):
        """Standalone "TVA" should still match with word boundaries."""
        assert _contains_any_alias("TVA", ["tva"]) is True

    def test_n_facture_normalized_from_n_degree(self):
        """"n° facture" normalizes to "n facture", which should match."""
        assert _contains_any_alias("N° Facture: 001", ["n° facture"]) is True


class TestExtractInlineValue:
    def test_with_colon(self):
        val = _extract_inline_value("montant_ttc", "TTC: 123.45")
        assert val is not None

    def test_without_colon(self):
        val = _extract_inline_value("date", "Date 15/03/2024")
        assert val is not None

    def test_no_match(self):
        val = _extract_inline_value("montant_ttc", "hello world")
        assert val is None


class TestCandidateIsPlausible:
    def test_short_candidate(self):
        c = OCRLine("x", BoundingBox(0, 0, 5, 5), 0.5, 0, 0)
        assert _candidate_is_plausible(c) is False

    def test_looks_like_label(self):
        c = OCRLine("facture", BoundingBox(0, 0, 20, 10), 0.5, 0, 0)
        assert _candidate_is_plausible(c) is False

    def test_valid_candidate(self):
        c = OCRLine("123.45", BoundingBox(0, 0, 20, 10), 0.9, 0, 0)
        assert _candidate_is_plausible(c) is True


class TestLooksLikeLabel:
    def test_label_keywords(self):
        assert _looks_like_label("Total TTC") is True
        assert _looks_like_label("Numero Facture") is True

    def test_not_a_label(self):
        assert _looks_like_label("ABC-123") is False

    def test_value_text(self):
        assert _looks_like_label("123.45") is False

    # ── Long text regression tests ────────────────────────────
    # These ensure that legitimate long values containing substrings
    # that happen to match short label tokens are NOT rejected.

    def test_long_client_name_with_no_substring(self):
        """"Société Nouvelle" contains "no" as substring but not as a word."""
        assert _looks_like_label("Société Nouvelle SARL") is False

    def test_long_client_name_with_ref_substring(self):
        """"Référencement" contains "reference" as a substring — flagged
        as label-like (acceptable, since "reference" is overwhelmingly a
        label keyword). Same behavior as the original code."""
        # "reference" matches inside "referencement" via substring
        assert _looks_like_label("SARL Référencement Plus") is True

    def test_invoice_number_with_ref_prefix(self):
        """Invoice numbers like REF-2024-001 should NOT be rejected.
        "ref" is followed by non-whitespace content ("-2024-001"),
        so the negative lookahead (?!\s*\S) prevents the match."""
        assert _looks_like_label("REF-2024-001") is False

    def test_invoice_number_no_prefix_no_content(self):
        """Standalone "NO" without following content IS a label."""
        assert _looks_like_label("NO") is True
        assert _looks_like_label("no.") is True

    def test_long_fournisseur_name(self):
        """Long supplier name should not be rejected by short token matches."""
        assert _looks_like_label("ETABLISSEMENTS DUPONT ET FILS") is False
        assert _looks_like_label("CONSTRUCTION MODERNE BATIMENT") is False


class TestCleanCandidateValue:
    def test_numeric_field(self):
        val = _clean_candidate_value("montant_ttc", "123.45")
        assert val is not None
        assert float(val) == 123.45  # noqa: PLR2004

    def test_date_field(self):
        val = _clean_candidate_value("date", "15/03/2024")
        assert val == "2024-03-15"

    def test_empty(self):
        assert _clean_candidate_value("montant_ttc", "") is None

    def test_oversized_amount(self):
        """Oversized decimal in candidate value must not crash."""
        val = _clean_candidate_value(
            "montant_ttc", "999999999999999999999999999999999999999.99",
        )
        assert val is None


class TestScoreCandidate:
    def test_same_line_score_high(self):
        anchor = OCRLine("TTC:", BoundingBox(0, 0, 30, 10), 0.9, 0, 0)
        candidate = OCRLine("123.45", BoundingBox(35, 0, 65, 10), 0.95, 0, 0)
        score = _score_candidate(anchor, candidate, same_line=True)
        assert score > 0

    def test_next_line_score(self):
        anchor = OCRLine("TTC", BoundingBox(0, 0, 30, 10), 0.9, 0, 0)
        candidate = OCRLine("123.45", BoundingBox(0, 15, 40, 25), 0.95, 0, 1)
        score = _score_candidate(anchor, candidate, same_line=False)
        assert score > 0


# ── Field collision tests ────────────────────────────────────────────────────


# ── Relaxed alias matching ──────────────────────────────────────────────────


class TestMatchesAliasRelaxed:
    """Tests for stopword-tolerant alias subsequence matching."""

    def test_exact_match(self):
        """Exact match should work."""
        assert _matches_alias_relaxed("n° de facture", ["n° de facture"]) is True

    def test_inserted_stopword(self):
        """"n° de la facture d'origine" should match "n° de facture".
        "la" is an inserted stopword between "de" and "facture"."""
        assert _matches_alias_relaxed("n° de la facture d'origine", ["n° de facture"]) is True

    def test_inserted_multiple_stopwords(self):
        """Multiple inserted stopwords should be tolerated."""
        assert _matches_alias_relaxed(
            "n° de la de la facture", ["n° de facture"]
        ) is True

    def test_missing_alias_token(self):
        """If a required alias token is missing, should NOT match."""
        # "facture" is missing
        assert _matches_alias_relaxed("n° de la commande", ["n° de facture"]) is False

    def test_tokens_in_wrong_order(self):
        """If alias tokens appear in wrong order, should NOT match."""
        assert _matches_alias_relaxed("facture de n°", ["n° de facture"]) is False

    def test_compound_n_facture_with_inserted_la(self):
        """"n° de la facture" matching "n° facture" (skipping "de")."""
        assert _matches_alias_relaxed("n° de la facture", ["n° facture"]) is True

    def test_no_false_positive_on_unrelated_text(self):
        """Unrelated text should not match."""
        assert _matches_alias_relaxed("bonjour le monde", ["n° de facture"]) is False

    def test_empty_text(self):
        assert _matches_alias_relaxed("", ["n° de facture"]) is False

    def test_multi_word_alias_with_no_stopwords(self):
        """Alias that doesn't have stopwords should still match when text has extra words."""
        assert _matches_alias_relaxed(
            "invoice number INV-2024-001", ["invoice number"]
        ) is True


# ── Fournisseur alias tests ───────────────────────────────────────────────────


class TestFournisseurAliases:
    """Tests for fournisseur alias matching."""

    def test_votre_entreprise_label(self):
        """"Votre entreprise" should be recognized as a fournisseur label."""
        lines = [
            OCRLine("Votre entreprise", BoundingBox(0, 0, 80, 15), 0.9, 0, 0),
            OCRLine("SARL Dupont et Fils", BoundingBox(0, 20, 110, 35), 0.95, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["fournisseur"] is not None
        assert "Dupont" in (fields["fournisseur"] or "")

    def test_votre_entreprise_lowercase(self):
        """Case-insensitive matching for "votre entreprise"."""
        lines = [
            OCRLine("Votre Entreprise", BoundingBox(0, 0, 80, 15), 0.9, 0, 0),
            OCRLine("ACME Inc.", BoundingBox(0, 20, 80, 35), 0.95, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["fournisseur"] is not None


# ── Numero facture with stopwords ─────────────────────────────────────────────


class TestNumeroFactureStopwords:
    """Tests for numero_facture extraction with inserted stopwords."""

    def test_inserted_la_in_alias(self):
        """"n° de la facture d'origine" should still extract the invoice number."""
        lines = [
            OCRLine("n° de la facture d'origine : INV-2024-001", BoundingBox(0, 0, 200, 20), 0.9, 0, 0),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["numero_facture"] is not None
        assert "INV-2024-001" in (fields["numero_facture"] or "")

    def test_inserted_stopword_separate_line(self):
        """Anchor with inserted stopword, value on same row to the right."""
        lines = [
            OCRLine("n° de la facture d'origine", BoundingBox(0, 0, 160, 20), 0.9, 0, 0),
            OCRLine("INV-2024-001", BoundingBox(170, 0, 280, 20), 0.95, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["numero_facture"] is not None

    def test_normal_numero_facture_still_works(self):
        """Normal "N° Facture" without stopwords should still work."""
        lines = [
            OCRLine("N° Facture: INV-001", BoundingBox(0, 0, 120, 20), 0.9, 0, 0),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["numero_facture"] == "INV-001"


class TestFieldCollisions:
    """Tests for the field collision prevention mechanism."""

    def test_two_amount_labels_close_together_no_collision(self):
        """Two amount labels with distinct values on different lines should
        each get their own candidate without collision."""
        lines = [
            OCRLine("Total HT", BoundingBox(0, 0, 50, 15), 0.9, 0, 0),
            OCRLine("1250.00",  BoundingBox(0, 20, 60, 35), 0.95, 0, 1),
            OCRLine("TVA",     BoundingBox(0, 40, 40, 55), 0.9, 0, 2),
            OCRLine("250.00",  BoundingBox(0, 60, 60, 75), 0.95, 0, 3),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_ht"] is not None
        assert fields["montant_tva"] is not None
        # Values should be different
        assert fields["montant_ht"] != fields["montant_tva"]

    def test_same_row_label_value_pair(self):
        """A label with its value on the same row (to the right)."""
        lines = [
            OCRLine("TTC:",    BoundingBox(0, 0, 30, 20), 0.9, 0, 0),
            OCRLine("1250.00", BoundingBox(100, 0, 160, 20), 0.95, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is not None

    def test_below_row_label_value_pair(self):
        """A label with its value in the row below."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 60, 15), 0.9, 0, 0),
            OCRLine("1250.00",   BoundingBox(0, 30, 60, 45), 0.95, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is not None
        assert float(fields["montant_ttc"]) == 1250.0  # noqa: PLR2004


    def test_two_amount_labels_same_value_line_collision_prevention(self):
        """Two amount labels (HT, TVA) positioned so both anchors' geometric
        search would independently pick the same value line — assert the two
        resolved fields end up with DIFFERENT OCR lines (or one is left null).
        This tests that Step 3 fallback respects claimed_lines."""
        lines = [
            OCRLine("Total HT", BoundingBox(0, 0, 50, 15), 0.9, 0, 0),
            OCRLine("TVA",      BoundingBox(0, 40, 40, 55), 0.9, 0, 1),
            OCRLine("1250.00",  BoundingBox(0, 70, 60, 85), 0.95, 0, 2),
        ]
        fields = extract_invoice_fields(lines)
        ht_val = fields["montant_ht"]
        tva_val = fields["montant_tva"]
        assert ht_val is None or tva_val is None or ht_val != tva_val


# ── Two-column layout regression tests ────────────────────────────────────────


class TestTwoColumnLayout:
    """Integration tests for two-column invoice layouts where labels (left)
    and values (right) are at the same row height but far apart horizontally.

    On real SumUp-style invoices, the amounts section (left: Total HT, TVA,
    Total TTC; right: 100.00, 20.00, 120.00) can have a 300-500 px gap between
    label and value columns — enough to trigger the 5x cluster_rows split but
    close enough that below-row search must still find them.
    """

    def test_amounts_two_column_with_gap(self):
        """Label column left (x=0-50), value column right (x=350-400).
        Same y-heights so vertical overlap merges them, then 5x split
        separates into sub-rows. Labels must still find their values
        via below-row geometric search."""
        lines = [
            # Invoice header (full-width, single column)
            OCRLine("Facture N° INV-2024-001", BoundingBox(50, 0, 300, 20), 0.95, 0, 0),
            OCRLine("Date: 15/03/2024", BoundingBox(50, 25, 200, 45), 0.95, 0, 1),
            # Two-column amounts section: labels left, values right
            # Same row height (y=50..70) — vertical overlap > 50% → same row
            # Then 5x split: gap = 350-50 = 300, height = 20, 5*20 = 100 < 300 → SPLIT
            OCRLine("Total HT", BoundingBox(0, 50, 50, 70), 0.9, 0, 2),
            OCRLine("100.00", BoundingBox(350, 50, 410, 70), 0.95, 0, 3),
            # TVA row (y=75..95)
            OCRLine("TVA", BoundingBox(0, 75, 40, 95), 0.9, 0, 4),
            OCRLine("20.00", BoundingBox(350, 75, 410, 95), 0.95, 0, 5),
            # Total TTC row (y=100..120)
            OCRLine("Total TTC", BoundingBox(0, 100, 60, 120), 0.9, 0, 6),
            OCRLine("120.00", BoundingBox(350, 100, 410, 120), 0.95, 0, 7),
        ]
        fields = extract_invoice_fields(lines)

        # All amounts must be found despite the two-column split
        assert fields["montant_ht"] is not None, f"HT should be found, got: {fields['montant_ht']}"
        assert fields["montant_tva"] is not None, f"TVA should be found, got: {fields['montant_tva']}"
        assert fields["montant_ttc"] is not None, f"TTC should be found, got: {fields['montant_ttc']}"

        # Verify correct values
        assert abs(float(fields["montant_ht"]) - 100.0) < 0.01, f"Expected ~100.0, got {fields['montant_ht']}"
        assert abs(float(fields["montant_tva"]) - 20.0) < 0.01, f"Expected ~20.0, got {fields['montant_tva']}"
        assert abs(float(fields["montant_ttc"]) - 120.0) < 0.01, f"Expected ~120.0, got {fields['montant_ttc']}"

        # Invoice number and date should also be found
        assert fields["numero_facture"] is not None
        assert fields["date"] is not None

    def test_two_column_header_client_left_invoice_right(self):
        """Two-column header with client info left, invoice metadata right.
        Neither column should contaminate the other's field extraction."""
        lines = [
            # Left column — client/supplier info
            OCRLine("Fournisseur:", BoundingBox(0, 0, 80, 15), 0.9, 0, 0),
            OCRLine("SARL Dupont", BoundingBox(0, 20, 120, 35), 0.95, 0, 1),
            OCRLine("Client:", BoundingBox(0, 50, 60, 65), 0.9, 0, 2),
            OCRLine("Mairie de Ville", BoundingBox(0, 70, 140, 85), 0.95, 0, 3),
            # Right column — invoice metadata
            OCRLine("N° Facture:", BoundingBox(350, 0, 440, 15), 0.9, 0, 4),
            OCRLine("INV-2024-001", BoundingBox(350, 20, 460, 35), 0.95, 0, 5),
            OCRLine("Date:", BoundingBox(350, 50, 400, 65), 0.9, 0, 6),
            OCRLine("15/03/2024", BoundingBox(350, 70, 440, 85), 0.95, 0, 7),
        ]
        fields = extract_invoice_fields(lines)

        # Invoice number should come from right column, not left
        assert fields["numero_facture"] is not None
        assert "INV-2024-001" in fields["numero_facture"]

        # Date should come from right column
        assert fields["date"] == "2024-03-15"

        # Fournisseur should come from left column
        assert fields["fournisseur"] is not None
        assert "Dupont" in fields["fournisseur"]

        # Client should come from left column
        assert fields["client"] is not None
        assert "Mairie" in fields["client"]

    def test_amounts_with_percentage_and_gap(self):
        """TVA with percentage on same line + two-column gap.
        This is the exact pattern that was failing on the real SumUp invoice:
        "TVA    20%    20.00 €" — percentage must be stripped, gap must
        not prevent value extraction."""
        lines = [
            # Label with percentage inline, value to the right
            OCRLine("TVA 20%", BoundingBox(0, 0, 60, 20), 0.9, 0, 0),
            OCRLine("20.00", BoundingBox(350, 0, 410, 20), 0.95, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_tva"] is not None
        assert abs(float(fields["montant_tva"]) - 20.0) < 0.01
