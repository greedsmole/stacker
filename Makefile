.DEFAULT_GOAL := help

PYTHON ?= python3
RID ?= host
RIDS := win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64

.PHONY: help publish publish-all $(RIDS)
# Publishing targets share .NET intermediate files; keep builds sequential even with -j.
.NOTPARALLEL:

help:
	@echo "make publish                  Package for this computer"
	@echo "make publish RID=linux-x64    Package for a specific target"
	@echo "make linux-arm64              Shortcut for a specific target"
	@echo "make publish-all              Package all six desktop targets"
	@echo "Targets: $(RIDS)"
	@echo "Requires .NET 10 SDK, Python 3 and GNU Make. Override PYTHON if needed."

publish:
	$(PYTHON) scripts/package.py "$(RID)"

$(RIDS):
	$(PYTHON) scripts/package.py "$@"

publish-all: $(RIDS)
