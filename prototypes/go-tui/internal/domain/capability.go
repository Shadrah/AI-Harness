package domain

import (
	"fmt"
	"slices"
	"strings"
)

// Support describes how faithfully a model/runtime combination provides a
// capability. Unknown is intentionally distinct from unsupported.
type Support string

const (
	SupportNative      Support = "native"
	SupportEmulated    Support = "emulated"
	SupportUnsupported Support = "unsupported"
	SupportUnknown     Support = "unknown"
)

type CapabilityID string

const (
	CapabilityTextInput        CapabilityID = "input.text"
	CapabilityImageInput       CapabilityID = "input.image"
	CapabilityAudioInput       CapabilityID = "input.audio"
	CapabilityVideoInput       CapabilityID = "input.video"
	CapabilityFileInput        CapabilityID = "input.file"
	CapabilityTextOutput       CapabilityID = "output.text"
	CapabilityImageOutput      CapabilityID = "output.image"
	CapabilityAudioOutput      CapabilityID = "output.audio"
	CapabilityStructuredOutput CapabilityID = "output.structured"
	CapabilityToolCalling      CapabilityID = "tools.function"
	CapabilityParallelTools    CapabilityID = "tools.parallel"
	CapabilityMCP              CapabilityID = "tools.mcp"
	CapabilityWebSearch        CapabilityID = "tools.web_search"
	CapabilityFileSearch       CapabilityID = "tools.file_search"
	CapabilityComputerUse      CapabilityID = "tools.computer_use"
	CapabilityShell            CapabilityID = "tools.shell"
	CapabilityFileEditing      CapabilityID = "tools.file_editing"
	CapabilityApprovals        CapabilityID = "agent.approvals"
	CapabilityResume           CapabilityID = "agent.resume"
	CapabilitySubagents        CapabilityID = "agent.subagents"
	CapabilityReasoning        CapabilityID = "model.reasoning"
	CapabilityStreaming        CapabilityID = "transport.streaming"
)

type ImageConstraints struct {
	MIMETypes    []string `json:"mime_types,omitempty"`
	Sources      []string `json:"sources,omitempty"`
	DetailLevels []string `json:"detail_levels,omitempty"`
	MaxImages    int      `json:"max_images,omitempty"`
	MaxBytes     int64    `json:"max_bytes,omitempty"`
}

type Capability struct {
	ID               CapabilityID      `json:"id"`
	Support          Support           `json:"support"`
	Reason           string            `json:"reason,omitempty"`
	Values           []string          `json:"values,omitempty"`
	ImageConstraints *ImageConstraints `json:"image_constraints,omitempty"`
}

type ModelProfile struct {
	Provider     string                      `json:"provider"`
	Runtime      string                      `json:"runtime"`
	Model        string                      `json:"model"`
	DisplayName  string                      `json:"display_name"`
	Capabilities map[CapabilityID]Capability `json:"capabilities"`
	Extensions   map[string]any              `json:"extensions,omitempty"`
}

func (p ModelProfile) Capability(id CapabilityID) Capability {
	if capability, ok := p.Capabilities[id]; ok {
		return capability
	}
	return Capability{ID: id, Support: SupportUnknown, Reason: "adapter did not declare this capability"}
}

type Requirement struct {
	Capability CapabilityID
	Value      string
}

type NegotiationIssue struct {
	Requirement Requirement
	Support     Support
	Message     string
}

func Negotiate(profile ModelProfile, requirements []Requirement) []NegotiationIssue {
	var issues []NegotiationIssue
	for _, requirement := range requirements {
		capability := profile.Capability(requirement.Capability)
		if capability.Support == SupportUnsupported || capability.Support == SupportUnknown {
			issues = append(issues, NegotiationIssue{
				Requirement: requirement,
				Support:     capability.Support,
				Message:     fmt.Sprintf("%s is %s for %s", requirement.Capability, capability.Support, profile.DisplayName),
			})
			continue
		}
		if requirement.Value != "" && len(capability.Values) > 0 && !slices.Contains(capability.Values, requirement.Value) {
			issues = append(issues, NegotiationIssue{
				Requirement: requirement,
				Support:     capability.Support,
				Message:     fmt.Sprintf("%s does not support value %q", requirement.Capability, requirement.Value),
			})
		}
	}
	return issues
}

// RequirementsForContent derives hard runtime requirements from the actual
// request. Adapters must never discard an unsupported modality silently.
func RequirementsForContent(content []ContentBlock) []Requirement {
	seen := make(map[CapabilityID]bool)
	var requirements []Requirement
	for _, block := range content {
		var id CapabilityID
		switch block.Kind {
		case ContentText:
			id = CapabilityTextInput
		case ContentImage:
			id = CapabilityImageInput
		case ContentAudio:
			id = CapabilityAudioInput
		case ContentVideo:
			id = CapabilityVideoInput
		case ContentFile:
			id = CapabilityFileInput
		}
		if id != "" && !seen[id] {
			seen[id] = true
			requirements = append(requirements, Requirement{Capability: id})
		}
	}
	return requirements
}

// ValidateContent checks both the coarse model capability and the constraints
// for each multimodal content block.
func ValidateContent(profile ModelProfile, content []ContentBlock) []NegotiationIssue {
	issues := Negotiate(profile, RequirementsForContent(content))
	imageCapability := profile.Capability(CapabilityImageInput)
	constraints := imageCapability.ImageConstraints
	if constraints == nil || (imageCapability.Support != SupportNative && imageCapability.Support != SupportEmulated) {
		return issues
	}

	imageCount := 0
	for _, block := range content {
		if block.Kind != ContentImage {
			continue
		}
		imageCount++
		if block.Resource == nil {
			issues = append(issues, contentIssue(profile, "image content is missing a resource"))
			continue
		}
		if len(constraints.Sources) > 0 && !slices.Contains(constraints.Sources, string(block.Resource.Source)) {
			issues = append(issues, contentIssue(profile, fmt.Sprintf("image source %q is not supported", block.Resource.Source)))
		}
		if len(constraints.MIMETypes) > 0 && block.Resource.MIMEType != "" && !containsFold(constraints.MIMETypes, block.Resource.MIMEType) {
			issues = append(issues, contentIssue(profile, fmt.Sprintf("image MIME type %q is not supported", block.Resource.MIMEType)))
		}
		if len(constraints.DetailLevels) > 0 && block.ImageDetail != "" && !slices.Contains(constraints.DetailLevels, string(block.ImageDetail)) {
			issues = append(issues, contentIssue(profile, fmt.Sprintf("image detail %q is not supported", block.ImageDetail)))
		}
		if constraints.MaxBytes > 0 && block.Resource.Bytes > constraints.MaxBytes {
			issues = append(issues, contentIssue(profile, fmt.Sprintf("image is %d bytes; maximum is %d", block.Resource.Bytes, constraints.MaxBytes)))
		}
	}
	if constraints.MaxImages > 0 && imageCount > constraints.MaxImages {
		issues = append(issues, contentIssue(profile, fmt.Sprintf("request has %d images; maximum is %d", imageCount, constraints.MaxImages)))
	}
	return issues
}

func contentIssue(profile ModelProfile, message string) NegotiationIssue {
	return NegotiationIssue{
		Requirement: Requirement{Capability: CapabilityImageInput},
		Support:     profile.Capability(CapabilityImageInput).Support,
		Message:     message,
	}
}

func containsFold(values []string, value string) bool {
	for _, candidate := range values {
		if strings.EqualFold(candidate, value) {
			return true
		}
	}
	return false
}
