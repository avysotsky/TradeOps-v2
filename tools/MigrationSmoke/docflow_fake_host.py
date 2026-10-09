"""Synthetic no-credential provider injected into real DocFlow ASGI HTTP host.

The DocFlow application code is loaded unmodified from pinned source; only the
provider is replaced to ensure reproducible no-network integration.
"""
from docflow_worker.engines import (
    SchemaDrivenTextExtractionBackend,
    SchemaDrivenTextExtractionBackendResult,
)
from docflow_worker.http_api import create_app


class SyntheticBackend(SchemaDrivenTextExtractionBackend):
    @property
    def name(self) -> str:
        return "mig02_synthetic_provider"

    async def extract(self, content, *, request, document_name=None):
        assert content
        return SchemaDrivenTextExtractionBackendResult(
            data={"category": "generic"}, confidence=None
        )


def backend_factory(model: str) -> SyntheticBackend:
    if model != "model-test":
        raise ValueError("Unsupported synthetic model")
    return SyntheticBackend()


app = create_app(backend_factory=backend_factory)
