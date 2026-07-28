# 🚀 Service Pulse - Health Monitoring Dashboard

## Overview

Service Pulse is a new real-time health monitoring dashboard for DevToolbox that allows you to track the status, performance, and availability of your services and APIs.

## Features Created

### 🎯 Core Functionality
- **Real-time Monitoring**: Continuously ping configured endpoints at customizable intervals
- **Health Status Tracking**: Monitor Online/Offline/Degraded/Maintenance states
- **Performance Metrics**: Track response times, success rates, uptime/downtime
- **Visual Dashboard**: Modern, responsive UI with status cards and mini charts
- **YAML Configuration**: Easy-to-edit service configuration storage

### 📊 Dashboard Components
- **Overall Stats Cards**: 
  - Online/Offline service counts
  - Average response time across all services
  - Overall uptime percentage
- **Service Cards**: Individual service monitoring with:
  - Real-time status indicators
  - Success rate and response time metrics
  - Mini ping history charts (last 15 pings)
  - Service environment and tags
  - Manual ping capability

### ⚙️ Service Management
- **Add/Edit Services**: Modal dialog for service configuration
- **Service Configuration**:
  - Name and endpoint URL
  - Ping interval (10-3600 seconds)
  - Timeout settings (5-120 seconds)
  - Environment categorization
  - Tagging system
  - Enable/disable monitoring

## Files Created

### Models
- `DevToolbox.Services/Models/ServiceHealthModels.cs`
  - `ServiceHealthConfig` - Root configuration
  - `ServiceEndpoint` - Service endpoint configuration
  - `ServiceHealth` - Service health state
  - `PingResult` - Individual ping result
  - `HealthMetrics` - Calculated metrics
  - `ServiceStatus` enum

### Services
- `DevToolbox.Services/Interfaces/IHealthMonitoringService.cs` - Service interface
- `DevToolbox.Services/Services/HealthMonitoringService.cs` - Core monitoring service

### UI Components
- `DevToolbox.UI/Pages/ServicePulse.razor` - Main dashboard page
- `DevToolbox.UI/Components/Dialogs/AddServiceDialog.razor` - Service management modal

### Configuration
- Navigation tab added to `NavMenu.razor`
- Service registration in `Program.cs`
- Sample YAML configuration with demo services

## Technical Architecture

### Service Architecture
- **Monitoring Service**: Background timers for each enabled service
- **HTTP Client**: Configurable timeouts and error handling
- **YAML Storage**: Persistent configuration using existing storage service
- **Event System**: Real-time updates via ServiceHealthChanged events

### UI Architecture
- **Reactive Updates**: 5-second refresh timer + event-driven updates
- **Modern Styling**: Matches existing DevToolbox design system
- **Performance Optimized**: Limits ping history to last 100 results per service
- **Responsive Design**: Grid-based layout adapts to screen sizes

## Default Sample Services

The system creates sample services on first run:
1. **Google Search** - Basic web connectivity test
2. **GitHub API** - API service monitoring example
3. **HTTPBin Test** - HTTP testing service for validation

## Navigation

Access Service Pulse through the new "Service Pulse" tab in the navigation menu (with heart-pulse icon).

## Configuration Storage

Services are stored in YAML format under the key `service_health_config` using the existing YAML storage service.

## Future Enhancement Ideas

- **Alerting System**: Email/notifications for service failures
- **Historical Data**: Long-term storage of metrics and trends
- **Dependency Mapping**: Service dependency visualization
- **Custom Health Checks**: Beyond HTTP GET requests
- **Export/Import**: Configuration backup and sharing
- **API Integration**: REST API for external monitoring tools