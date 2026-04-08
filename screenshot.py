"""Generate architecture screenshot from docs/architecture.html.

Usage: python screenshot.py
Requires: pip install playwright && playwright install chromium
"""

import os
from pathlib import Path
from playwright.sync_api import sync_playwright

script_dir = Path(__file__).resolve().parent
html_path = (script_dir / "docs" / "architecture.html").as_uri()
output_path = str(script_dir / "docs" / "architecture-screenshot.png")

with sync_playwright() as p:
    browser = p.chromium.launch()
    context = browser.new_context(
        viewport={"width": 1440, "height": 1200},
        device_scale_factor=2,  # 2x DPI for crisp rendering
    )
    page = context.new_page()
    page.goto(html_path)
    page.wait_for_timeout(1500)  # Let fonts + CSS settle

    # Screenshot the .frame element directly — no white border
    frame = page.query_selector(".frame")
    if frame:
        frame.screenshot(path=output_path, type="png")
        print(f"Element screenshot saved: {output_path}")
    else:
        page.screenshot(path=output_path, type="png", full_page=True)
        print(f"Full page screenshot saved: {output_path}")

    browser.close()
