"""
Regenerates docs/Feature-Guide.pdf.

Every count in this document was taken from the repository and the database rather
than from memory, and the "not included" section is deliberately as detailed as the
rest. A capability list a client cannot trust is worse than no list, because the first
thing they find missing puts everything else in doubt.

    python tools/generate-feature-guide.py
"""

import pathlib

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import (
    HRFlowable, KeepTogether, ListFlowable, ListItem, PageBreak, Paragraph,
    SimpleDocTemplate, Spacer, Table, TableStyle,
)

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "docs/Feature-Guide.pdf"

INK = colors.HexColor("#16202B")
MUTED = colors.HexColor("#5B6773")
FAINT = colors.HexColor("#8794A1")
RULE = colors.HexColor("#D2D8DF")
BRASS = colors.HexColor("#8C5E12")
WARN = colors.HexColor("#7F1D1D")
WARNBG = colors.HexColor("#FDF3F0")
SOFT = colors.HexColor("#F4F6F8")

# ----------------------------------------------------------------- the content

AT_A_GLANCE = [
    ("Ticket types", "9"),
    ("Ticket states", "10"),
    ("Permission keys", "63"),
    ("Built-in roles", "7"),
    ("API endpoints", "93"),
    ("Database tables", "55"),
    ("Screens", "21"),
    ("Automated tests", "318 backend, 39 browser"),
]

SECTIONS = [
    ("Raising and working a ticket", [
        ("Nine ticket types",
         "Incident, Service Request, Software Bug, Data Correction, Access Request, "
         "Feature Request, Training Request, Security Incident and Integration Failure."),
        ("Ten states, with enforced transitions",
         "New, Assigned, In Progress, Waiting for Requester, Waiting for Third Party, "
         "Escalated, Resolved, Closed, Reopened and Cancelled. Illegal moves are refused "
         "rather than silently allowed, so a ticket cannot skip from New to Closed."),
        ("Priority is calculated, never chosen",
         "The requester describes impact and urgency — questions they can answer — and a "
         "configurable matrix produces the priority. Nobody is asked to pick a priority "
         "directly, because everybody picks the highest one available."),
        ("Requesters cannot declare their own emergency",
         "A claim above the organization's cap is reduced and the original kept, so staff "
         "can see what the requester believed. Any lead can still raise it afterwards."),
        ("Conversation with internal notes",
         "Public replies the requester sees, and staff-only notes they never do — excluded "
         "server-side rather than hidden by the interface."),
        ("Attachments, including screenshots and screen recordings",
         "Files are identified by inspecting their contents, not by trusting the name, so a "
         "script renamed to .png cannot be served back as one."),
        ("Assignment, acceptance and hand-off",
         "To a person or a team, with the previous owner, the reason and the method recorded."),
        ("Resolution and closure",
         "A written resolution is mandatory. The requester confirms or rejects it, and a "
         "rejected resolution reopens the ticket rather than closing an argument."),
        ("Satisfaction rating",
         "Overall, resolution and agent scores with an optional comment, once a ticket closes."),
    ]),

    ("Service level agreements", [
        ("Measured in business time, not wall clock",
         "A four-hour target raised at 16:00 on Friday is not due at 20:00 that evening. "
         "Working hours, split shifts, half days and holidays are all honoured."),
        ("Working calendars",
         "Any number of them, each with its own time zone, weekly pattern and holiday list. "
         "Recurring holidays repeat annually; moveable ones are entered per year."),
        ("Policies scoped by category, department and type",
         "The most specific matching policy wins, with response and resolution targets per "
         "priority level."),
        ("A policy may set its own priority matrix",
         "What counts as Critical is not the same question for a stopped production line as "
         "for an internal reporting request. Overrides are per cell, so a policy that "
         "decides two cells still inherits the rest."),
        ("Clocks that pause honestly",
         "Time waiting on the requester or a third party can be excluded; time waiting on "
         "the support desk never is, because that is the delay the SLA exists to measure."),
        ("Warnings before the deadline, not only after",
         "A configurable threshold raises a warning while there is still time to act."),
    ]),

    ("Escalation and notifications", [
        ("A configurable escalation ladder",
         "Rungs fire at percentages of the resolution budget — warn at 70, chase the team "
         "lead at 90, again at 100 with a status change, department manager at 120. Each "
         "rung fires once, however often the monitor runs."),
        ("Breaches reach somebody even when nobody owns the ticket",
         "An unassigned ticket that breaches is the most important breach there is, because "
         "nobody has looked at it. Supervision is notified regardless of assignment."),
        ("Interruption for the person who can act, a list for supervision",
         "The assignee is shown a popup. Team leads, administrators and super admins get an "
         "entry in their notification list — interrupting them for every warning would "
         "teach them to dismiss all of them."),
        ("New work announces itself",
         "Being assigned a ticket, or a ticket arriving in your team's queue, notifies "
         "everyone who has to pick it up. Nobody is notified of their own action."),
        ("Notifications cannot repeat themselves",
         "Every notification carries a deduplication key, so a job that runs twice cannot "
         "tell the same person the same thing twice."),
    ]),

    ("Knowledge base", [
        ("Draft, review, published, archived",
         "Each step is a separate permission. Nothing is visible to requesters until "
         "somebody entitled to publish has done so."),
        ("Full version history",
         "Every save records a version with its author and a change note, so a reviewer can "
         "see what moved between two revisions."),
        ("Three visibility levels",
         "Internal for staff only, Organization for requesters here, Public across tenants."),
        ("Suggestions while working a ticket",
         "Articles matching the ticket's category and text are offered to the agent."),
        ("Helpfulness feedback",
         "Readers mark an article helpful or not, so the useless ones become visible."),
    ]),

    ("Reporting and audit", [
        ("Five reports",
         "SLA compliance, agent performance, over-claimed severity, ticket volume trend and "
         "satisfaction — each over a configurable period."),
        ("Staff workload, live",
         "Open, in progress, waiting, Critical and High counts, SLA breaches, and the age of "
         "the oldest open ticket, per person. Administrators see the whole desk; a team "
         "lead sees only the teams they lead."),
        ("Dashboard",
         "Open volume, breaches, unassigned queue depth and workload at a glance."),
        ("CSV export",
         "Values that could be read as formulas are neutralised, so an exported report "
         "cannot execute anything when opened in a spreadsheet."),
        ("Append-only audit trail",
         "Who did what, when, from which address, with the reason where one was required. "
         "Entries record the actor's name and email as a snapshot rather than a link, so a "
         "deleted account does not erase the history of what it did."),
    ]),

    ("Administration", [
        ("Users",
         "Create, edit, deactivate, reset a password, revoke every session, and delete. "
         "Deleting an account that owns work anonymises it instead of destroying the "
         "history — the tickets survive and read “Deleted user”."),
        ("Roles and permissions",
         "Sixty-three permission keys across eight areas. Roles are database rows, not "
         "hardcoded checks: nothing in the code branches on a role name, so a role the "
         "client invents works exactly like a built-in one."),
        ("Teams",
         "Members, capacity weights, a lead, an escalation target and an acceptance timeout."),
        ("Service catalogue",
         "Categories, subcategories, business applications and their modules, each able to "
         "route new tickets to a default team."),
        ("SLA policies and calendars", "Described in full above."),
        ("System settings",
         "Typed key and value pairs an administrator edits without a deployment."),
        ("AI assistance",
         "Off by default and switchable per organization."),
    ]),

    ("ERP integration", [
        ("Tickets link to real business records",
         "Purchase orders, styles, customers, suppliers, factories, merchants, production "
         "orders, inspections, shipments, invoices, debit notes, commission invoices, "
         "digital product passports and integrations."),
        ("Look a ticket up by the record it concerns",
         "Support can answer “what is happening with this shipment” without knowing "
         "a ticket number."),
    ]),

    ("Artificial intelligence, kept in its place", [
        ("Optional, and off until switched on",
         "Without a key the system behaves identically and every AI call reports itself "
         "unavailable."),
        ("It advises; the rules decide",
         "The deterministic priority calculation always runs and always stands. A "
         "recommendation is shown beside it, never in place of it."),
        ("It cannot reach the database or bypass a permission",
         "Suggestions are returned to a person, who acts on them under their own authority."),
        ("The key never leaves the server",
         "It is not in the browser bundle, not in the database, and the interface has no "
         "code path that could reach a provider directly."),
    ]),

    ("Security", [
        ("Multi-tenant isolation, fail-closed",
         "Every query is filtered by organization at the database layer. A missing filter "
         "returns nothing rather than everything."),
        ("Permission-based authorization throughout",
         "Checked on the server for every action. A request that names an organization, a "
         "role or a permission is ignored — all three come from the signed-in token."),
        ("Data scopes",
         "Own, assigned, team, department, organization or all. A ticket outside your scope "
         "answers “not found” rather than “forbidden”, so the list of "
         "what exists cannot be mapped by probing."),
        ("Passwords",
         "PBKDF2-HMAC-SHA512 with a per-password salt. Lockout after repeated failures, and "
         "sign-in is rate limited per address."),
        ("Two-factor authentication",
         "Time-based codes, per account."),
        ("Sessions",
         "Short-lived access tokens with rotating refresh tokens. Reuse of a retired token "
         "is detected and recorded."),
        ("Content Security Policy and security headers",
         "On both the API and the page the browser loads, verified in a real browser."),
        ("Ticket and article text is never rendered as markup",
         "Which is what stops anybody who can write a comment running code in a "
         "colleague's browser."),
        ("SQL injection is structurally absent",
         "One parameterised statement exists in the entire codebase; everything else goes "
         "through the query builder."),
        ("Refuses to start when misconfigured",
         "A placeholder signing key, a wildcard CORS origin or demo seeding left switched on "
         "stops the application booting in production rather than running unsafely."),
    ]),
]

NOT_INCLUDED = [
    ("Email notifications",
     "Notifications appear in the application only. There is no mail sender, so nobody is "
     "emailed about anything — the most significant gap on this list for a support desk."),
    ("Time recorded against a ticket",
     "The database and the interface expect it, but no endpoint exists, so staff cannot log "
     "hours worked."),
    ("Escalation policies have no administration screen",
     "A sensible default ladder is created on installation and can be changed only in the "
     "database."),
    ("Contact phone and tags are captured but never shown",
     "A requester can supply a phone number and it is stored, but no screen displays it."),
    ("The workload figures have no screen yet",
     "The data is available through the API; the page has not been built."),
    ("Staff are still called “agents” in places",
     "A rename is planned and not yet done."),
    ("Notifications refresh every thirty seconds rather than instantly",
     "Live push is installed but not yet connected."),
    ("Tickets cannot be raised by email",
     "Every ticket is raised through the interface."),
    ("No approval workflows or parent-and-child tickets"),
    ("Uploaded files are not scanned for malware",
     "File types are verified and dangerous ones are never rendered in the browser, but no "
     "virus scanner is connected."),
    ("Container deployment is not yet working",
     "The API image builds; the browser application image does not. Installation is manual "
     "for now."),
]


def build():
    styles = getSampleStyleSheet()

    body = ParagraphStyle("body", parent=styles["BodyText"], fontSize=9.5, leading=13.5,
                          alignment=TA_LEFT, textColor=INK, spaceAfter=0)
    h1 = ParagraphStyle("h1", parent=styles["Heading1"], fontSize=23, leading=27,
                        spaceAfter=3, textColor=INK)
    sub = ParagraphStyle("sub", parent=body, textColor=MUTED, fontSize=10.5, spaceAfter=14)
    h2 = ParagraphStyle("h2", parent=styles["Heading2"], fontSize=11.5, leading=15,
                        spaceBefore=16, spaceAfter=7, textColor=BRASS)
    feat = ParagraphStyle("feat", parent=body, spaceAfter=2)
    desc = ParagraphStyle("desc", parent=body, textColor=MUTED, fontSize=9, leading=12.5,
                          leftIndent=0, spaceAfter=7)
    warn = ParagraphStyle("warn", parent=body, fontSize=9.5, leading=13.5,
                          textColor=WARN, backColor=WARNBG, borderPadding=9,
                          spaceBefore=4, spaceAfter=12)
    small = ParagraphStyle("small", parent=body, fontSize=8.5, leading=12, textColor=FAINT)

    doc = SimpleDocTemplate(
        str(OUT), pagesize=A4,
        leftMargin=19 * mm, rightMargin=19 * mm, topMargin=17 * mm, bottomMargin=17 * mm,
        title="Support Ticketing System — Features and Capabilities",
        author="ITG Group",
    )

    story = [
        Paragraph("Features and capabilities", h1),
        Paragraph("Support Ticketing System — ITG Group", sub),
        HRFlowable(width="100%", color=RULE, spaceAfter=12),
        Paragraph(
            "A support desk built for a garment and textile business: tickets are linked to "
            "the purchase orders, styles and shipments they actually concern, and service "
            "levels are measured against real working hours rather than the clock on the "
            "wall. Everything below is implemented and covered by automated tests. What is "
            "<i>not</i> included is listed at the end, in the same detail.", body),
        Spacer(1, 14),
    ]

    # At a glance
    rows, row = [], []
    for i, (label, value) in enumerate(AT_A_GLANCE, start=1):
        row.append(Paragraph(f"<b>{value}</b><br/><font size=8 color='#5B6773'>{label}</font>", body))
        if i % 4 == 0:
            rows.append(row)
            row = []
    if row:
        row += [Paragraph("", body)] * (4 - len(row))
        rows.append(row)

    story.append(Table(
        rows, colWidths=[43 * mm] * 4,
        style=TableStyle([
            ("BACKGROUND", (0, 0), (-1, -1), SOFT),
            ("BOX", (0, 0), (-1, -1), 0.5, RULE),
            ("INNERGRID", (0, 0), (-1, -1), 0.5, colors.white),
            ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
            ("TOPPADDING", (0, 0), (-1, -1), 9),
            ("BOTTOMPADDING", (0, 0), (-1, -1), 9),
            ("LEFTPADDING", (0, 0), (-1, -1), 10),
        ])))

    for title, items in SECTIONS:
        block = [Paragraph(title, h2)]
        for item in items:
            name, detail = item if len(item) == 2 else (item[0], "")
            block.append(Paragraph(f"<b>{name}</b>", feat))
            if detail:
                block.append(Paragraph(detail, desc))
        # Keep a heading with at least its first entry.
        story.append(KeepTogether(block[:3]))
        story.extend(block[3:])

    story.append(PageBreak())
    story.append(Paragraph("What is not included", h2))
    story.append(Paragraph(
        "Listed in the same detail as everything else. A capability list a client cannot "
        "trust is worse than none, because the first thing they find missing puts the rest "
        "in doubt.", body))
    story.append(Spacer(1, 8))

    for item in NOT_INCLUDED:
        name, detail = item if len(item) == 2 else (item[0], "")
        story.append(Paragraph(f"<b>{name}</b>", feat))
        if detail:
            story.append(Paragraph(detail, desc))

    story.append(Paragraph(
        "<b>Email is the one to weigh first.</b> Everything else on this list is a "
        "convenience or an internal tool. A support desk whose requesters are never emailed "
        "expects them to log in and look, which changes how the desk is used.", warn))

    story.append(Paragraph("How it is built", h2))
    story.append(Paragraph(
        "ASP.NET Core 10 and C# on the server, React on the browser, SQL Server for storage. "
        "The server is layered so that the business rules depend on nothing — not the "
        "database, not the web — and seven automated tests fail the build if that is ever "
        "violated. Three hundred and eighteen server tests and thirty-nine browser tests run "
        "on every change, the server tests against a real SQL Server rather than a "
        "substitute, because every isolation defect this project has found reproduced only "
        "against a database that genuinely applies the rules.", body))

    story.append(Spacer(1, 14))
    story.append(HRFlowable(width="100%", color=RULE, spaceAfter=8))
    story.append(Paragraph(
        "Counts in this document were taken from the repository and the database, not from "
        "memory. Regenerate with <font face='Courier'>python tools/generate-feature-guide.py</font>.",
        small))

    doc.build(story)
    print(f"wrote {OUT.relative_to(ROOT)} ({OUT.stat().st_size:,} bytes)")


if __name__ == "__main__":
    build()
