# Shared Dockerfile for the Go services.
# Build context = the module folder; --build-arg MAIN=./cmd/connector (or . for fakebank)
FROM golang:1.27-alpine AS build
ARG MAIN=.
WORKDIR /src
COPY go.mod go.sum* ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/app ${MAIN}

FROM alpine:3.23
RUN adduser -D -H app
COPY --from=build /out/app /app
USER app
EXPOSE 8080
ENTRYPOINT ["/app"]
