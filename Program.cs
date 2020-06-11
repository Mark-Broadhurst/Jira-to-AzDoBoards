using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Atlassian.Jira;
using Microsoft.Extensions.Configuration;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Newtonsoft.Json;

namespace AzureBoardsMigration
{
    public static class Program
    {
        private static readonly string MigratedPath =
            Path.Combine(Environment.CurrentDirectory, "..", "..", "migrated.json");

        private static readonly IDictionary<string, int> Migrated = File.Exists(MigratedPath)
            ? JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(MigratedPath))
            : new Dictionary<string, int>();

        private static IConfiguration config;

        public static async Task Main(string[] args)
        {
            config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .Build();

            var vstsConnection = new VssConnection(new Uri(config["AzureDevOps:Url"]),
                new VssBasicCredential(string.Empty, config["AzureDevOps:PersonalAccessToken"]));
            var witClient = vstsConnection.GetClient<WorkItemTrackingHttpClient>();

            var jiraConn = Jira.CreateRestClient(config["Jira:Url"], config["Jira:UserId"], config["Jira:Password"]);

            IList<Issue> issues;
            DateTime? lastCreated = new DateTime(2010, 1, 1);
            do
            {
                issues = jiraConn.Issues.Queryable
                    .Where(p => p.Project == config["Jira:Project"] && p.Created > lastCreated)
                    .OrderBy(p => p.Created)
                    .ToList();

                // By default this will root the migrated items at the root of Vsts project
                // Uncomment ths line and provide an epic id if you want everything to be
                // a child of Vsts epic

                foreach (var issue in issues)
                {
                    Console.Write($"{issue.Key} - {issue.Type.Name} ");
                    lastCreated = issue.Created;
                    try
                    {
                        switch (issue.Type.Name)
                        {
                            case "Epic":
                                await CreateFeature(witClient, issue);
                                break;
                            case "Bug":
                                await CreateBug(witClient, issue);
                                break;
                            case "Improvement":
                            case "Story":
                            case "Spike":
                            case "Knowledge Transfer":
                            case "Support Incident":
                                await CreateBacklogItem(witClient, issue);
                                break;
                            case "Task":
                            case "Sub-task":
                                await CreateTask(witClient, issue);
                                break;
                            default:
                                throw new ArgumentException("Not supporting", issue.Type.Name);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                }
            } while (issues.Any());
        }

        private static Task CreateFeature(WorkItemTrackingHttpClient client, Issue jira) => CreateWorkItem(client, "Feature", jira, jira.ParentIssueKey, jira.CustomFields["Epic Name"].Values[0], jira.Description ?? jira.Summary, ResolveFeatureState(jira.Status));
        private static Task CreateBug(WorkItemTrackingHttpClient client, Issue jira) => CreateWorkItem(client, "Bug", jira, jira.CustomFields["Epic Link"]?.Values[0] ?? jira.ParentIssueKey, jira.Summary, jira.Description, ResolveBacklogItemState(jira.Status));
        private static Task CreateBacklogItem(WorkItemTrackingHttpClient client, Issue jira) => CreateWorkItem(client, "Product Backlog Item", jira, jira.CustomFields["Epic Link"]?.Values[0] ?? jira.ParentIssueKey, jira.Summary, jira.Description,  ResolveBacklogItemState(jira.Status));
        private static Task CreateTask(WorkItemTrackingHttpClient client, Issue jira) => CreateWorkItem(client, "Task", jira, jira.ParentIssueKey, jira.Summary, jira.Description, ResolveTaskState(jira.Status));

        private static async Task CreateWorkItem(
            WorkItemTrackingHttpClient client,
            string type,
            Issue jira,
            string parentKey,
            string title,
            string description,
            string state,
            params JsonPatchOperation[] fields)
        {
            // Short-circuit if we've already processed this item.
            if (Migrated.ContainsKey(jira.Key.Value))
            {
                return;
            }

            var vsts = new JsonPatchDocument
                {
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.State", 
                        Value = state
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.CreatedBy", 
                        Value = ResolveUser(jira.Reporter)
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.CreatedDate", 
                        Value = jira.Created.Value.ToUniversalTime()
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.ChangedBy", 
                        Value = ResolveUser(jira.Reporter)
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.ChangedDate", 
                        Value = jira.Created.Value.ToUniversalTime()
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.Title", 
                        Value = title
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.Description", 
                        Value = description
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/Microsoft.VSTS.Common.Priority", 
                        Value = ResolvePriority(jira.Priority)
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/Microsoft.VSTS.Common.ClosedDate",
                        Value = jira.ResolutionDate
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/Microsoft.VSTS.Scheduling.FinishDate",
                        Value = jira.ResolutionDate
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/Microsoft.VSTS.Common.ResolvedDate",
                        Value = jira.ResolutionDate
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/Microsoft.VSTS.Common.ResolvedReason",
                        Value = jira.Resolution?.Name
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.IterationPath",
                        Value = ResolveIteration(jira.CustomFields["Sprint"]?.Values, config["AzureDevOps:Project"])
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/Microsoft.VSTS.Scheduling.StoryPoints", 
                        Value = jira.CustomFields["Story Points"]?.Values[0]
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/Microsoft.VSTS.Scheduling.Effort", 
                        Value = jira.CustomFields["Story Points"]?.Values[0]
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.AreaPath",
                        Value = ResolveAreaPath(jira.CustomFields["DC Team"]?.Values[0], config["AzureDevOps:Project"])
                    },
                    new JsonPatchOperation
                    {
                        Path = "/fields/System.AssignedTo",
                        Value = ResolveUser(jira.Assignee)
                    }
                }
                ;
            if (parentKey != null)
            {
                vsts.Add(new JsonPatchOperation
                {
                    Path = "/relations/-",
                    Value = new WorkItemRelation
                    {
                        Rel = "System.LinkTypes.Hierarchy-Reverse",
                        Url = $"https://{config["AzureDevOps:Url"]}/_apis/wit/workItems/{Migrated[parentKey]}"
                    }
                });
            }

            if (jira.Labels.Any())
            {
                vsts.Add(new JsonPatchOperation
                {
                    Path = "/fields/System.Tags",
                    Value = jira.Labels.Aggregate("", (l, r) => $"{l};{r}").Trim(';', ' ')
                });
            }

            foreach (var attachment in await jira.GetAttachmentsAsync())
            {
                var path = Path.GetTempFileName();
                attachment.Download(path);

                await using var stream = new MemoryStream(await File.ReadAllBytesAsync(path));

                var uploaded = await client.CreateAttachmentAsync(stream, config["AzureDevOps:Project"], fileName: attachment.FileName);
                vsts.Add(new JsonPatchOperation
                {
                    Path = "/relations/-", 
                    Value = new WorkItemRelation
                    {
                        Rel = "AttachedFile", 
                        Url = uploaded.Url
                    }
                });

                File.Delete(path);
            }

            var all = vsts.Concat(fields)
                .Where(p => p.Value != null)
                .ToList();
            vsts = new JsonPatchDocument();
            vsts.AddRange(all);
            var workItem = await client.CreateWorkItemAsync(vsts, config["AzureDevOps:Project"], type, bypassRules: true);
            AddMigrated(jira.Key.Value, workItem.Id.Value);

            await CreateComments(client, workItem.Id.Value, jira);

            Console.WriteLine($"Added {type}: {jira.Key}{title}");

        }

        private static object ResolveAreaPath(string value, string project)
        {
            return value == null 
                ? null 
                : $"{project}\\{value}";
        }

        private static string ResolveIteration(string[] sprints, string project)
        {
            if (sprints == null || !sprints.Any()) return null;

            return $"{project}\\{sprints.First()}";
        }

        private static string ResolveFeatureState(IssueStatus state)
        {
            return state.Name switch
            {
                "Needs Approval" => "New",
                "Ready for Review" => "In Progress",
                "Closed" => "Done",
                "Resolved" => "Done",
                "Reopened" => "New",
                "In Progress" => "In Progress",
                "Backlog" => "New",
                "Selected for Development" => "New",
                "Open" => "New",
                "To Do" => "New",
                "DONE" => "Done",
                "Done" => "Done",
                _ => throw new ArgumentException("Could not find state", nameof(state))
            };
        }

        private static string ResolveBacklogItemState(IssueStatus state)
        {
            return state.Name switch
            {
                "Needs Approval" => "New",
                "Ready for Review" => "Committed",
                "Closed" => "Done",
                "Resolved" => "Done",
                "Reopened" => "New",
                "In Progress" => "Committed",
                "In Development" => "Committed",
                "In Testing" => "Committed",
                "3 Amigos" => "Committed",
                "Backlog" => "New",
                "Selected for Development" => "Approved",
                "Open" => "Approved",
                "User Acceptance" => "Approved",
                "To Do" => "New",
                "DONE" => "Done",
                "Done" => "Done",
                "Rejected" => "Removed",
                _ => throw new ArgumentException("Could not find state", nameof(state))
            };
        }

        private static string ResolveTaskState(IssueStatus state)
        {
            return state.Name switch
            {
                "Needs Approval" => "To Do",
                "Ready for Review" => "In Progress",
                "In Development" => "In Progress",
                "Closed" => "Done",
                "Resolved" => "Done",
                "Reopened" => "To Do",
                "In Progress" => "In Progress",
                "Backlog" => "To Do",
                "Selected for Development" => "To Do",
                "Open" => "To Do",
                "To Do" => "To Do",
                "DONE" => "Done",
                "Done" => "Done",
                "Rejected" => "Removed",
                _ => throw new ArgumentException("Could not find state", nameof(state))
            };
        }

        private static int ResolvePriority(IssuePriority priority)
        {
            return priority.Name switch
            {
                "Lowest" => 4,
                "Low" => 4,
                "Medium" => 3,
                "High" => 2,
                "Highest" => 2,
                "Blocker" => 1,
                _ => throw new ArgumentException("Could not find priority", nameof(priority))
            };
        }

        private static string ResolveUser(string user)
        {
            return user;

            // Provide your own user mapping
            switch (user)
            {
                case null:
                    return null;
                default:
                    throw new ArgumentException("Could not find user", nameof(user));
            }
        }

        private static async Task CreateComments(WorkItemTrackingHttpClient client, int id, Issue jira)
        {
            var comments = (await jira.GetCommentsAsync())
                .Select(p => CreateComment(p.Body, p.Author, p.CreatedDate?.ToUniversalTime()))
                .Concat(new[] {CreateComment($"Migrated from {jira.Key}")}).ToList();
            foreach (var comment in comments)
            {
                await client.UpdateWorkItemAsync(comment, id, bypassRules: true);
            }
        }

        private static JsonPatchDocument CreateComment(string comment, string username = null, DateTime? date = null)
        {
            var patch = new JsonPatchDocument
            {
                new JsonPatchOperation
                {
                    Path = "/fields/System.History",
                    Value = comment
                }
            };
            if (username != null)
                patch.Add(new JsonPatchOperation
                {
                    Path = "/fields/System.ChangedBy",
                    Value = ResolveUser(username)
                });
            if (date != null)
                patch.Add(new JsonPatchOperation
                {
                    Path = "/fields/System.ChangedDate",
                    Value = date?.ToUniversalTime()
                });

            return patch;
        }

        private static void AddMigrated(string jira, int vsts)
        {
            if (Migrated.ContainsKey(jira)) return;

            Migrated.Add(jira, vsts);
            File.WriteAllText(MigratedPath, JsonConvert.SerializeObject(Migrated));
        }
    }
}