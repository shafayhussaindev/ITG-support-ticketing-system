"""
Regenerates docs/QA-Test-Credentials.pdf.

The sheet exists because a tester needs to switch between roles quickly and the
interface gives no hint of what each one can reach. Keeping it generated rather than
hand-written means it can be reissued after a database reset without anybody having to
remember which accounts the seeder makes.

    python tools/generate-qa-credentials.py

The password must match Seed:RoleAccountPassword in appsettings.Development.json. It is
read from there rather than repeated here, so the two cannot drift apart.
"""

import json
import pathlib
import re
import sys

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import (
    HRFlowable, PageBreak, Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle,
)

ROOT = pathlib.Path(__file__).resolve().parent.parent
SETTINGS = ROOT / "src/SupportTicketing.Api/appsettings.Development.json"
OUT = ROOT / "docs/QA-Test-Credentials.pdf"

# Role, address, and what a tester should be able to confirm from that account. The
# permission counts are the seeded defaults; an administrator who edits a role changes
# them, which is itself worth knowing when a number here stops matching the interface.
ACCOUNTS = [
    ("Super Admin", "superadmin@itg.test", 55,
     "Everything, including deleting users and editing system settings."),
    ("Administrator", "administrator@itg.test", 17,
     "Users, roles, teams, categories and SLA policies. Not reporting."),
    ("Manager", "manager@itg.test", 42,
     "Reports, the audit log and every ticket in the organization. No administration."),
    ("Team Lead", "lead@itg.test", 35,
     "Their team's queue, assignment and escalation, plus reports."),
    ("Technical Specialist", "specialist@itg.test", 25,
     "Tickets assigned to their team, including internal notes."),
    ("Support Agent", "agent@itg.test", 23,
     "Their team's tickets: comment, resolve, log work."),
    ("Requester", "requester@itg.test", 10,
     "Only tickets they raised. Must never see an internal note."),
]

CHECKS = [
    ("Scope is enforced, not hidden",
     "Sign in as the Requester and open a ticket ID raised by somebody else. The answer "
     "must be 404, not 403 — a 403 would confirm the ticket exists."),
    ("Internal notes stay internal",
     "As an Agent, add a comment marked internal. Sign in as the Requester who raised "
     "that ticket. The note must not appear, in the page or in the network response."),
    ("Administration is closed to Managers",
     "As the Manager, open Administration → Users. Expect to be refused; the Manager "
     "role carries reporting, not user management."),
    ("A deleted person keeps their history",
     "As Super Admin, delete an account that raised a ticket. The ticket must survive "
     "and read “Deleted user” rather than disappearing."),
    ("Passwords are not recoverable",
     "Create a user in Administration. The temporary password is shown once. Reload "
     "before using it and it is gone — reset it rather than guessing."),
]


def demo_password() -> str:
    """Reads the password out of appsettings, tolerating comments in the JSON."""
    raw = SETTINGS.read_text(encoding="utf-8")
    stripped = re.sub(r"^\s*//.*$", "", raw, flags=re.MULTILINE)
    seed = json.loads(stripped).get("Seed", {})
    password = seed.get("RoleAccountPassword")

    if not password:
        sys.exit(
            f"Seed:RoleAccountPassword is not set in {SETTINGS.relative_to(ROOT)}.\n"
            "The accounts this sheet documents would not exist, so there is nothing to "
            "write down."
        )

    return password


def build() -> None:
    password = demo_password()

    styles = getSampleStyleSheet()
    body = ParagraphStyle("body", parent=styles["BodyText"], fontSize=9.5, leading=13.5,
                          alignment=TA_LEFT, spaceAfter=6)
    h1 = ParagraphStyle("h1", parent=styles["Heading1"], fontSize=18, leading=22,
                        spaceAfter=2, textColor=colors.HexColor("#0f172a"))
    h2 = ParagraphStyle("h2", parent=styles["Heading2"], fontSize=12, leading=16,
                        spaceBefore=14, spaceAfter=6, textColor=colors.HexColor("#0f172a"))
    sub = ParagraphStyle("sub", parent=body, textColor=colors.HexColor("#64748b"),
                         fontSize=9, spaceAfter=12)
    warn = ParagraphStyle("warn", parent=body, fontSize=9.5, leading=13.5,
                          textColor=colors.HexColor("#7f1d1d"),
                          backColor=colors.HexColor("#fef2f2"),
                          borderPadding=8, spaceBefore=6, spaceAfter=12)
    cell = ParagraphStyle("cell", parent=body, fontSize=8.5, leading=11.5, spaceAfter=0)
    mono = ParagraphStyle("mono", parent=cell, fontName="Courier-Bold", fontSize=8.5)

    doc = SimpleDocTemplate(
        str(OUT), pagesize=A4,
        leftMargin=18 * mm, rightMargin=18 * mm, topMargin=16 * mm, bottomMargin=16 * mm,
        title="Support Ticketing System — QA test credentials",
        author="ITG Group",
    )

    story = [
        Paragraph("QA test credentials", h1),
        Paragraph("Support Ticketing System — ITG Group", sub),
        HRFlowable(width="100%", color=colors.HexColor("#e2e8f0"), spaceAfter=12),

        Paragraph(
            "One account per role, for testing what each role can and cannot reach. "
            "All seven share the same password.", body),

        Paragraph(
            "<b>These accounts exist only in Development.</b> They are created by "
            "RoleAccountSeeder, which refuses to run under any other environment name, "
            "and the application refuses to start in Production if the flag that enables "
            "them is switched on. Do not create equivalents on a live system: one shared "
            "password written down in a document is exactly the arrangement this system "
            "is built to prevent everywhere else.", warn),

        Paragraph("Sign in", h2),
    ]

    story.append(Table(
        [[Paragraph("<b>Address</b>", cell), Paragraph("http://localhost:5173", mono)],
         [Paragraph("<b>Password</b>", cell), Paragraph(password, mono)],
         [Paragraph("<b>Organization</b>", cell), Paragraph("ITG Group (ITG)", cell)]],
        colWidths=[30 * mm, 144 * mm],
        style=TableStyle([
            ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
            ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
            ("TOPPADDING", (0, 0), (-1, -1), 6),
            ("LINEBELOW", (0, 0), (-1, -2), 0.4, colors.HexColor("#e2e8f0")),
        ])))

    story += [
        Paragraph("Accounts", h2),
        Table(
            [[Paragraph("<b>Role</b>", cell), Paragraph("<b>Email</b>", cell),
              Paragraph("<b>Perms</b>", cell), Paragraph("<b>Should be able to</b>", cell)]]
            + [[Paragraph(role, cell), Paragraph(email, mono),
                Paragraph(str(count), cell), Paragraph(reach, cell)]
               for role, email, count, reach in ACCOUNTS],
            colWidths=[32 * mm, 46 * mm, 14 * mm, 82 * mm],
            repeatRows=1,
            style=TableStyle([
                ("VALIGN", (0, 0), (-1, -1), "TOP"),
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#f1f5f9")),
                ("LINEBELOW", (0, 0), (-1, 0), 0.6, colors.HexColor("#cbd5e1")),
                ("LINEBELOW", (0, 1), (-1, -2), 0.3, colors.HexColor("#e2e8f0")),
                ("TOPPADDING", (0, 0), (-1, -1), 6),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
                ("LEFTPADDING", (0, 0), (-1, -1), 6),
            ])),

        Paragraph(
            "Permission counts are the seeded defaults. They change the moment an "
            "administrator edits a role, so a count here that no longer matches "
            "Administration → Roles is a sign the role was edited, not that this sheet "
            "is broken.", sub),

        PageBreak(),
        Paragraph("Worth checking first", h2),
        Paragraph(
            "These are the behaviours most likely to be wrong in a system of this kind, "
            "and the quickest to confirm.", body),
    ]

    for i, (title, detail) in enumerate(CHECKS, start=1):
        story.append(Paragraph(f"<b>{i}. {title}</b>", body))
        story.append(Paragraph(detail, ParagraphStyle(
            "check", parent=body, leftIndent=12, spaceAfter=10)))

    story += [
        Spacer(1, 8),
        Paragraph("Notes", h2),
        Paragraph(
            "<b>Team-scoped roles see nothing until they are on a team.</b> Team Lead, "
            "Technical Specialist and Support Agent have a Team data scope, so their "
            "queues are empty until an administrator adds them to a team under "
            "Administration → Teams. An empty queue for those three is configuration, "
            "not a defect.", body),
        Paragraph(
            "<b>Sign-ins are rate limited</b> to ten per minute from one address. "
            "Switching between all seven accounts in quick succession will start "
            "returning 401 — wait a minute rather than concluding a password is wrong.", body),
        Paragraph(
            "<b>Deleting an account is permanent.</b> There is no restore. To recreate "
            "these seven after deleting one, restart the API: the seeder adds only what "
            "is missing and leaves existing passwords alone.", body),
    ]

    doc.build(story)
    print(f"wrote {OUT.relative_to(ROOT)} ({OUT.stat().st_size:,} bytes)")


if __name__ == "__main__":
    build()
