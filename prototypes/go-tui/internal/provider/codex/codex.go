package codex

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os/exec"
	"slices"
	"strings"

	"github.com/will/harness/internal/domain"
	"github.com/will/harness/internal/provider"
	"github.com/will/harness/internal/provider/codex/appserver"
)

type Runtime struct {
	Executable string
}

func New() *Runtime { return &Runtime{Executable: "codex"} }

func (*Runtime) ID() string { return "codex" }

func (r *Runtime) Detect(ctx context.Context) (provider.Status, error) {
	path, err := exec.LookPath(r.Executable)
	if err != nil {
		return provider.Status{Message: "Codex CLI not found"}, nil
	}
	versionOutput, versionErr := exec.CommandContext(ctx, path, "--version").CombinedOutput()
	status := provider.Status{Available: true, Version: strings.TrimSpace(string(versionOutput))}
	loginOutput, loginErr := exec.CommandContext(ctx, path, "login", "status").CombinedOutput()
	if loginErr == nil {
		status.Authenticated = true
		status.Message = strings.TrimSpace(string(loginOutput))
	} else {
		status.Message = strings.TrimSpace(string(loginOutput))
	}
	if versionErr != nil {
		return status, fmt.Errorf("read Codex version: %w", versionErr)
	}
	return status, nil
}

func (*Runtime) Models(context.Context) ([]domain.ModelProfile, error) {
	// The app-server model/list response will replace this bootstrap profile.
	// Unknown fields are deliberate: a runtime must prove support before a run
	// requiring that feature is accepted.
	capabilities := map[domain.CapabilityID]domain.Capability{
		domain.CapabilityTextInput:  {ID: domain.CapabilityTextInput, Support: domain.SupportNative},
		domain.CapabilityTextOutput: {ID: domain.CapabilityTextOutput, Support: domain.SupportNative},
		domain.CapabilityImageInput: {
			ID:      domain.CapabilityImageInput,
			Support: domain.SupportNative,
			ImageConstraints: &domain.ImageConstraints{
				MIMETypes:    []string{"image/png", "image/jpeg", "image/webp", "image/gif"},
				Sources:      []string{"path", "url"},
				DetailLevels: []string{"auto", "low", "high", "original"},
			},
		},
		domain.CapabilityStreaming:   {ID: domain.CapabilityStreaming, Support: domain.SupportNative},
		domain.CapabilityApprovals:   {ID: domain.CapabilityApprovals, Support: domain.SupportNative},
		domain.CapabilityResume:      {ID: domain.CapabilityResume, Support: domain.SupportNative},
		domain.CapabilityShell:       {ID: domain.CapabilityShell, Support: domain.SupportNative},
		domain.CapabilityFileEditing: {ID: domain.CapabilityFileEditing, Support: domain.SupportNative},
		domain.CapabilityMCP:         {ID: domain.CapabilityMCP, Support: domain.SupportNative},
		domain.CapabilityReasoning:   {ID: domain.CapabilityReasoning, Support: domain.SupportUnknown, Reason: "resolved from app-server model metadata"},
	}
	return []domain.ModelProfile{{
		Provider:     "openai",
		Runtime:      "codex-app-server",
		Model:        "configured",
		DisplayName:  "Codex (configured model)",
		Capabilities: capabilities,
	}}, nil
}

// LiveModels asks the installed app-server for its current, account-aware model
// catalog. Input modalities and reasoning values are taken from the runtime
// response rather than guessed from the provider name.
func (r *Runtime) LiveModels(ctx context.Context) ([]domain.ModelProfile, error) {
	client, err := appserver.Start(ctx, r.Executable, io.Discard)
	if err != nil {
		return nil, err
	}
	defer client.Close()
	if err := client.Initialize(ctx); err != nil {
		return nil, err
	}
	response, err := client.Models(ctx)
	if err != nil {
		return nil, err
	}
	profiles := make([]domain.ModelProfile, 0, len(response.Data))
	for _, model := range response.Data {
		capabilities := runtimeCapabilities()
		capabilities[domain.CapabilityImageInput] = domain.Capability{
			ID:      domain.CapabilityImageInput,
			Support: supportFor(slices.Contains(model.InputModalities, "image")),
			ImageConstraints: &domain.ImageConstraints{
				MIMETypes:    []string{"image/png", "image/jpeg", "image/webp", "image/gif"},
				Sources:      []string{"path", "url"},
				DetailLevels: []string{"auto", "low", "high", "original"},
			},
		}
		values := make([]string, 0, len(model.SupportedReasoningEfforts))
		for _, option := range model.SupportedReasoningEfforts {
			values = append(values, option.ReasoningEffort)
		}
		capabilities[domain.CapabilityReasoning] = domain.Capability{
			ID: domain.CapabilityReasoning, Support: supportFor(len(values) > 0), Values: values,
		}
		profiles = append(profiles, domain.ModelProfile{
			Provider: "openai", Runtime: "codex-app-server", Model: model.Model,
			DisplayName: model.DisplayName, Capabilities: capabilities,
			Extensions: map[string]any{"id": model.ID, "is_default": model.IsDefault, "description": model.Description},
		})
	}
	return profiles, nil
}

func runtimeCapabilities() map[domain.CapabilityID]domain.Capability {
	return map[domain.CapabilityID]domain.Capability{
		domain.CapabilityTextInput:   {ID: domain.CapabilityTextInput, Support: domain.SupportNative},
		domain.CapabilityTextOutput:  {ID: domain.CapabilityTextOutput, Support: domain.SupportNative},
		domain.CapabilityStreaming:   {ID: domain.CapabilityStreaming, Support: domain.SupportNative},
		domain.CapabilityApprovals:   {ID: domain.CapabilityApprovals, Support: domain.SupportNative},
		domain.CapabilityResume:      {ID: domain.CapabilityResume, Support: domain.SupportNative},
		domain.CapabilityShell:       {ID: domain.CapabilityShell, Support: domain.SupportNative},
		domain.CapabilityFileEditing: {ID: domain.CapabilityFileEditing, Support: domain.SupportNative},
		domain.CapabilityMCP:         {ID: domain.CapabilityMCP, Support: domain.SupportNative},
	}
}

func supportFor(value bool) domain.Support {
	if value {
		return domain.SupportNative
	}
	return domain.SupportUnsupported
}

func (*Runtime) Run(context.Context, provider.Request) (<-chan domain.Event, error) {
	return nil, fmt.Errorf("Codex execution is not wired in this milestone; discovery and model negotiation are available")
}

func (*Runtime) Cancel(context.Context, string) error { return nil }

// DecodeNativeEvent is kept deliberately lossless. The normalized event can be
// enriched as the app-server protocol evolves without discarding new fields.
func DecodeNativeEvent(data []byte) (domain.Event, error) {
	if !json.Valid(data) {
		return domain.Event{}, fmt.Errorf("invalid Codex event JSON")
	}
	return domain.Event{Kind: domain.EventProviderNative, Provider: "openai", Native: append([]byte(nil), data...)}, nil
}
