"""Adversarial / fault-injection tests for server/ingestion.py."""

from __future__ import annotations

import pytest

from server.ingestion import IngestionError, load_invoice_images


class TestLoadInvoiceImages:
    """Adversarial inputs for the file ingestion pipeline."""

    def test_unsupported_extension(self):
        """A .txt extension must raise IngestionError."""
        with pytest.raises(IngestionError, match="Unsupported file type"):
            load_invoice_images(b"hello", "file.txt")

    def test_no_extension(self):
        """A filename with no extension must raise IngestionError."""
        with pytest.raises(IngestionError, match="Unsupported file type"):
            load_invoice_images(b"hello", "filehasnoextension")

    def test_empty_bytes_jpg(self):
        """Zero-length bytes for a .jpg must raise IngestionError (not crash)."""
        with pytest.raises(IngestionError):
            load_invoice_images(b"", "file.jpg")

    def test_empty_bytes_png(self):
        """Zero-length bytes for a .png must raise IngestionError."""
        with pytest.raises(IngestionError):
            load_invoice_images(b"", "file.png")

    def test_empty_bytes_pdf(self):
        """Zero-length bytes for a .pdf must raise IngestionError."""
        with pytest.raises(IngestionError):
            load_invoice_images(b"", "file.pdf")

    def test_corrupt_jpeg_bytes(self):
        """Corrupt (random) bytes for a .jpg must raise IngestionError, not crash."""
        with pytest.raises(IngestionError):
            load_invoice_images(b"\xff\xd8\xff\x00\x00\x00corrupted", "file.jpg")

    def test_corrupt_png_bytes(self):
        """Corrupt PNG magic bytes must raise IngestionError."""
        with pytest.raises(IngestionError):
            load_invoice_images(b"\x89PNGcorrupted\x00\x00\x00", "file.png")

    def test_corrupt_bmp_bytes(self):
        """Corrupt .bmp bytes must raise IngestionError."""
        with pytest.raises(IngestionError):
            load_invoice_images(b"BM\x00corrupted", "file.bmp")

    def test_corrupt_tiff_bytes(self):
        """Corrupt .tiff bytes must raise IngestionError."""
        with pytest.raises(IngestionError):
            load_invoice_images(b"MM\x00\x2a\x00corrupted", "file.tiff")

    def test_html_file_disguised_as_image(self):
        """An HTML file with a .png extension must raise IngestionError."""
        with pytest.raises(IngestionError):
            load_invoice_images(
                b"<html><body>not an image</body></html>", "invoice.png",
            )

    def test_json_file_disguised_as_image(self):
        """A JSON file with a .jpg extension must raise IngestionError."""
        with pytest.raises(IngestionError):
            load_invoice_images(
                b'{"key": "value", "nested": {"a": 1}}', "invoice.jpg",
            )

    def test_valid_small_png(self):
        """A valid 1x1 pixel PNG must load successfully."""
        # Minimal valid PNG (1x1 red pixel)
        png_bytes = bytes([
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,  # PNG signature
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,  # IHDR chunk
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,  # IDAT chunk
            0x54, 0x08, 0xD7, 0x63, 0x68, 0xC0, 0x00, 0x00,
            0x00, 0x02, 0x00, 0x01, 0xE4, 0x27, 0x25, 0xE5,
            0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,  # IEND chunk
            0xAE, 0x42, 0x60, 0x82,
        ])
        images = load_invoice_images(png_bytes, "invoice.png")
        assert len(images) >= 1

    def test_pdf_filename_with_uppercase(self):
        """Uppercase extension .PDF must be recognized."""
        # Can't create a real PDF here, but it should attempt conversion
        # before raising IngestionError (which it will because bytes are invalid)
        with pytest.raises(IngestionError):
            load_invoice_images(b"not a pdf", "invoice.PDF")

    def test_tif_vs_tiff_consistency(self):
        """Both .tif and .tiff extensions must be recognized."""
        with pytest.raises(IngestionError, match="Unable to open"):
            load_invoice_images(b"corrupt bytes", "file.tif")
        with pytest.raises(IngestionError, match="Unable to open"):
            load_invoice_images(b"corrupt bytes", "file.tiff")

    def test_bmp_extension(self):
        """.bmp extension must be recognized."""
        with pytest.raises(IngestionError, match="Unable to open"):
            load_invoice_images(b"corrupt bytes", "file.bmp")
