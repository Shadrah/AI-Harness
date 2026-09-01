package domain

import "encoding/json"

type ContentKind string

const (
	ContentText  ContentKind = "text"
	ContentImage ContentKind = "image"
	ContentAudio ContentKind = "audio"
	ContentVideo ContentKind = "video"
	ContentFile  ContentKind = "file"
)

type ResourceSource string

const (
	SourcePath       ResourceSource = "path"
	SourceURL        ResourceSource = "url"
	SourceInlineData ResourceSource = "inline_data"
	SourceProviderID ResourceSource = "provider_id"
)

type Resource struct {
	Source   ResourceSource `json:"source"`
	Value    string         `json:"value"`
	MIMEType string         `json:"mime_type,omitempty"`
	Name     string         `json:"name,omitempty"`
	Bytes    int64          `json:"bytes,omitempty"`
}

type ImageDetail string

const (
	ImageDetailAuto     ImageDetail = "auto"
	ImageDetailLow      ImageDetail = "low"
	ImageDetailHigh     ImageDetail = "high"
	ImageDetailOriginal ImageDetail = "original"
)

type ContentBlock struct {
	Kind        ContentKind     `json:"kind"`
	Text        string          `json:"text,omitempty"`
	Resource    *Resource       `json:"resource,omitempty"`
	ImageDetail ImageDetail     `json:"image_detail,omitempty"`
	Width       int             `json:"width,omitempty"`
	Height      int             `json:"height,omitempty"`
	Alt         string          `json:"alt,omitempty"`
	Extensions  json.RawMessage `json:"extensions,omitempty"`
}

func Text(text string) ContentBlock {
	return ContentBlock{Kind: ContentText, Text: text}
}

func Image(resource Resource, detail ImageDetail) ContentBlock {
	return ContentBlock{Kind: ContentImage, Resource: &resource, ImageDetail: detail}
}
